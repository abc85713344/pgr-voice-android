using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

/// <summary>原包压成普通台词的单项选择。只附加游戏停靠语义，不改变包图、听书或旧存档。</summary>
public sealed record SourceChoiceOffer(PlaybackEngine Owner, Node CurrentNode, Node OptionLineNode,
    string OptionLabel, Node? ActualAnswerNode, Node? NextChoiceNode, string SourceIdentity, string Reason,
    string Navigation, string SectionContract, bool AtOption);

public static class SourceChoicePolicy
{
    const string Resource = "PgrVoice.SourceChoices.20261009.json";
    static readonly Lazy<SourceChoiceCatalog> Catalog = new(() =>
    {
        using var stream = typeof(SourceChoicePolicy).Assembly.GetManifestResourceStream(Resource);
        if (stream == null) return new();
        return JsonSerializer.Deserialize<SourceChoiceCatalog>(stream, Json.Options) ?? new();
    });

    public static bool TryAt(PlaybackEngine engine, out SourceChoiceOffer? offer) => Create(engine, true, out offer, out _);
    public static bool TryNext(PlaybackEngine engine, out SourceChoiceOffer? offer, out int steps) => Create(engine, false, out offer, out steps);

    static bool Create(PlaybackEngine engine, bool at, out SourceChoiceOffer? offer, out int steps)
    {
        offer = null; steps = 0;
        if (engine.Pack.SchemaVersion != 3 || Catalog.Value.Version != 1 || engine.Current is not { Kind: "line" } current ||
            engine.Mode is not (RunMode.Following or RunMode.Ready or RunMode.Paused) || engine.ReviewRoute != null ||
            engine.ExportNavigation().Current.SingleLine) return false;
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.ConfirmCurrentPosition() || !Eligible(probe)) return false;
        Node? option = current;
        if (!at && !DefaultBranchPolicy.TryNext(probe, out option, out steps)) return false;
        if (option == null) return false;
        var rules = Catalog.Value.Singles.Where(r => r.PackId == engine.Pack.Id && r.Option.Id == option.Id).ToArray();
        if (rules.Length != 1 || !rules[0].Option.Matches(option) || !Plain(option) ||
            option.SectionId != current.SectionId || !engine.Allowed(option)) return false;
        var rule = rules[0];
        Node? answer = null, nextChoice = null;
        string reason = rule.Reason;
        // 只沿原图已核的无对白控制点接实际对白。连续单项菜单必须分别停靠，不能跨菜单找正文。
        var optionProbe = new PlaybackEngine(engine.Pack);
        bool knownNext = optionProbe.ImportNavigation(engine.ExportNavigation()) && optionProbe.ConfirmGameLine(option.Id) &&
            DefaultBranchPolicy.TryNext(optionProbe, out var following, out _) && following?.Id == rule.Answer?.Id;
        if (rule.Answer != null && rule.NextChoice == null && knownNext &&
            engine.Pack.ById.TryGetValue(rule.Answer.Id, out var candidate) && rule.Answer.Matches(candidate) &&
            Plain(candidate) && candidate.SectionId == option.SectionId &&
            !Catalog.Value.Singles.Any(r => r.PackId == engine.Pack.Id && r.Option.Id == candidate.Id))
        {
            var before = probe.ExportNavigation();
            if (probe.ConfirmGameLine(candidate.Id) && Eligible(probe) &&
                probe.Facts.SetEquals(before.Current.Facts) && probe.Heard.SetEquals(before.Current.Heard)) answer = candidate;
        }
        // 连续选择只接受源资料显式绑定的相邻选项，不把普通 Next 或后面的对白猜作目标。
        if (rule.Answer == null && rule.NextChoice is { } binding && option.NextId == binding.Id &&
            binding.Id != option.Id && engine.Pack.ById.TryGetValue(binding.Id, out var next) &&
            binding.Matches(next) && Plain(next) && next.SectionId == option.SectionId && next.PathId == option.PathId &&
            next.NextId != option.Id && engine.Allowed(next))
        {
            var nextRules = Catalog.Value.Singles.Where(r => r.PackId == engine.Pack.Id && r.Option.Id == next.Id).ToArray();
            var nextProbe = new PlaybackEngine(engine.Pack); var before = engine.ExportNavigation();
            if (nextRules.Length == 1 && nextRules[0].Option.Matches(next) &&
                nextProbe.ImportNavigation(before) && nextProbe.ConfirmGameLine(next.Id) && Eligible(nextProbe) &&
                nextProbe.Facts.SetEquals(before.Current.Facts) && nextProbe.Heard.SetEquals(before.Current.Heard) &&
                nextProbe.Choices.OrderBy(p => p.Key).SequenceEqual(before.Current.Choices.OrderBy(p => p.Key))) nextChoice = next;
        }
        if (answer == null && nextChoice == null && string.IsNullOrWhiteSpace(reason))
            reason = "这里需要在游戏中选择，但选后的对白尚不能可靠确认。请完成游戏选择后，按当前台词手动定位。";
        offer = new(engine, current, option, rule.OptionLabel, answer, nextChoice, rule.SourceIdentity, reason,
            Navigation(engine), Contract(engine.Pack, option.SectionId), at);
        return true;
    }

    public static bool TryResolve(PlaybackEngine engine, SourceChoiceOffer offer, out Node? answer, out string reason)
    {
        answer = null;
        if (!TryResolveOffer(engine, offer, out var current, out reason) || current == null) return false;
        if (current.ActualAnswerNode == null) { reason = current.Reason; return false; }
        answer = current.ActualAnswerNode; reason = ""; return true;
    }

    public static bool TryResolveOffer(PlaybackEngine engine, SourceChoiceOffer offer, out SourceChoiceOffer? resolved, out string reason)
    {
        resolved = null; reason = "选择确认已过期，请重新核对游戏当前对白。";
        if (!ReferenceEquals(engine, offer.Owner) || !ReferenceEquals(engine.Current, offer.CurrentNode) ||
            Navigation(engine) != offer.Navigation || Contract(engine.Pack, offer.OptionLineNode.SectionId) != offer.SectionContract ||
            !Create(engine, offer.AtOption, out var current, out _) || current == null ||
            current.OptionLineNode != offer.OptionLineNode || current.ActualAnswerNode != offer.ActualAnswerNode ||
            current.NextChoiceNode != offer.NextChoiceNode || current.OptionLabel != offer.OptionLabel ||
            current.SourceIdentity != offer.SourceIdentity || current.Reason != offer.Reason) return false;
        resolved = current; reason = ""; return true;
    }

    public static bool TryGetDisplayAnchor(PlaybackEngine engine, int number, out Node? first, out Node? answer)
    {
        first = answer = null;
        if (Catalog.Value.Version != 1 || engine.Pack.SchemaVersion != 3 || number < 1 ||
            engine.Current == null || engine.ReviewRoute != null || engine.ExportNavigation().Current.SingleLine) return false;
        var rules = Catalog.Value.Multiple.Where(r => r.PackId == engine.Pack.Id && r.Number == number &&
            (engine.CurrentId == r.MenuId && engine.Mode == RunMode.Choice || engine.CurrentId == r.First.Id && engine.Mode == RunMode.Following)).ToArray();
        if (rules.Length != 1) return false;
        var rule = rules[0];
        if (!engine.Pack.ById.TryGetValue(rule.MenuId, out var menu) || menu.Kind != "choice" || menu.Archived ||
            menu.MenuType != "exclusive" || menu.SetFacts.Count != 0 || !string.IsNullOrEmpty(menu.CompleteRoute) ||
            number > menu.Options.Count || menu.Options.Count != rule.MenuOptionIds.Count || !menu.Options.Select(o => o.Id).SequenceEqual(rule.MenuOptionIds) ||
            menu.Options.Any(o => o.Requires.Count != 0 || o.Excludes.Count != 0 || !string.IsNullOrWhiteSpace(o.ConditionNote))) return false;
        var option = menu.Options[number - 1];
        if (option.Id != rule.OptionId || option.Label != rule.OptionLabel || option.TargetId != rule.First.Id ||
            option.ReturnId != rule.ReturnId || option.MergeId != rule.MergeId || !option.SegmentIds.SequenceEqual(rule.SegmentIds) ||
            rule.ControlPath.Count < 2 || rule.ControlPath[0].Id != rule.First.Id || rule.ControlPath[^1].Id != rule.Answer.Id ||
            rule.ControlPath.Any(binding => !engine.Pack.ById.TryGetValue(binding.Id, out var control) || !binding.Matches(control)) ||
            rule.ControlPath.Zip(rule.ControlPath.Skip(1)).Any(pair => pair.First.NextId != pair.Second.Id) ||
            !option.BodyVerified || !option.ExitVerified || option.BodyEvidence.Count == 0 || option.ExitEvidence.Count == 0 ||
            option.BodyEvidence.Any(string.IsNullOrWhiteSpace) || option.ExitEvidence.Any(string.IsNullOrWhiteSpace) ||
            !engine.Pack.ById.TryGetValue(rule.First.Id, out var source) || !rule.First.Matches(source) ||
            !engine.Pack.ById.TryGetValue(rule.Answer.Id, out var target) || !rule.Answer.Matches(target) ||
            !Plain(source) || !Plain(target) || source.SectionId != menu.SectionId || target.SectionId != menu.SectionId ||
            Catalog.Value.Singles.Any(r => r.PackId == engine.Pack.Id && r.Option.Id == target.Id) ||
            source.PathId != option.PathId || !option.SegmentIds.Contains(source.Id)) return false;
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation())) return false;
        if (engine.Mode == RunMode.Choice)
        {
            if (!DefaultBranchPolicy.TryChoice(engine, number, out _, out var chosen) || chosen != source) return false;
            probe.Commit(menu.Id); probe.SelectBranch(probe.AvailableOptions.FindIndex(o => o.Id == option.Id));
        }
        else if (engine.Choices.GetValueOrDefault(menu.Id) != option.PathId || !engine.Allowed(source)) return false;
        var before = probe.ExportNavigation();
        if (!probe.ConfirmGameLine(target.Id) || !Eligible(probe) || !probe.Facts.SetEquals(before.Current.Facts) ||
            !probe.Heard.SetEquals(before.Current.Heard)) return false;
        first = source; answer = target; return true;
    }

    static bool Plain(Node node) => !node.Archived && node.Kind == "line" && node.Options.Count == 0 &&
        node.SetFacts.Count == 0 && string.IsNullOrEmpty(node.CompleteRoute) && !string.IsNullOrWhiteSpace(node.Text);
    static bool Eligible(PlaybackEngine engine) => engine.Current is { } node && Plain(node) && engine.Allowed(node) &&
        engine.Mode == RunMode.Following && engine.ReviewRoute == null && !engine.ExportNavigation().Current.SingleLine &&
        (engine.EvaluateCommonAutoPlayNext().CurrentIsCommon || BranchRouteContext.TryResumeAuto(engine, out _) || BranchRouteContext.IsVerifiedCommon(engine));
    static string Navigation(PlaybackEngine engine) => JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options);
    static string Contract(Pack pack, string section) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(pack.Nodes.Where(n => n.SectionId == section).OrderBy(n => n.Id, StringComparer.Ordinal), Json.Options))));
}

public sealed class SourceChoiceCatalog
{
    public int Version { get; set; } = 1;
    public List<SourceSingleChoiceRule> Singles { get; set; } = new();
    public List<SourceMultipleChoiceRule> Multiple { get; set; } = new();
}
public sealed class SourceLineBinding
{
    public string Id { get; set; } = "";
    public string SectionId { get; set; } = "";
    public string Text { get; set; } = "";
    public string Speaker { get; set; } = "";
    public string Source { get; set; } = "";
    public string PathId { get; set; } = "";
    public string? NextId { get; set; }
    public bool Matches(Node node) => node.Id == Id && node.SectionId == SectionId && node.Text == Text && node.Speaker == Speaker &&
        node.Source == Source && node.PathId == PathId && node.NextId == NextId;
}
public sealed class SourceSingleChoiceRule
{
    public string PackId { get; set; } = "";
    public string OptionLabel { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public string Reason { get; set; } = "";
    public SourceLineBinding Option { get; set; } = new();
    public SourceLineBinding? Answer { get; set; }
    public SourceLineBinding? NextChoice { get; set; }
}
public sealed class SourceMultipleChoiceRule
{
    public string PackId { get; set; } = "";
    public string MenuId { get; set; } = "";
    public List<string> MenuOptionIds { get; set; } = new();
    public int Number { get; set; }
    public string OptionId { get; set; } = "";
    public string OptionLabel { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public string? ReturnId { get; set; }
    public string? MergeId { get; set; }
    public List<string> SegmentIds { get; set; } = new();
    public List<SourceControlBinding> ControlPath { get; set; } = new();
    public SourceLineBinding First { get; set; } = new();
    public SourceLineBinding Answer { get; set; } = new();
}
public sealed class SourceControlBinding
{
    public string Id { get; set; } = "";
    public string SectionId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string Speaker { get; set; } = "";
    public string Source { get; set; } = "";
    public string PathId { get; set; } = "";
    public string? NextId { get; set; }
    public string CompleteRoute { get; set; } = "";
    public bool Matches(Node node) => !node.Archived && node.Id == Id && node.SectionId == SectionId && node.Kind == Kind &&
        (node.Text ?? "") == Text && (node.Speaker ?? "") == Speaker && (node.Source ?? "") == Source && node.PathId == PathId &&
        node.NextId == NextId && node.Options.Count == 0 && node.SetFacts.Count == 0 && (node.CompleteRoute ?? "") == CompleteRoute;
}
