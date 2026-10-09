using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

/// <summary>卡片始终同时保留原选项身份和待指认对白；预览只供对照，不授予推进权限。</summary>
public sealed record ConfirmedBranchCard(string Id, string MenuId, string OptionId, string OptionLabel,
    int OptionNumber, string NodeId, string Speaker, string Text, int Position, bool SkipsOptionLine,
    string PreviewSpeaker, string PreviewText);

/// <summary>一轮只读人工指认候选。调用方另绑定前台、屏幕、区域及会话代次。</summary>
public sealed class ConfirmedBranchOffer
{
    public string MenuId { get; }
    public string SectionId { get; }
    public IReadOnlyList<ConfirmedBranchCard> Cards { get; }
    public string Reason { get; }
    internal PlaybackEngine Engine { get; }
    internal Node? Menu { get; }
    internal string Navigation { get; }
    internal string Graph { get; }
    internal string SectionContract { get; }
    internal IReadOnlyDictionary<string, (ChoiceOption Option, Node Node)> Bindings { get; }

    internal ConfirmedBranchOffer(PlaybackEngine engine, Node? menu, string sectionId,
        List<ConfirmedBranchCard> cards, string reason, string navigation, string graph, string contract,
        Dictionary<string, (ChoiceOption Option, Node Node)> bindings)
    {
        Engine = engine; Menu = menu; MenuId = menu?.Id ?? ""; SectionId = sectionId;
        Cards = cards.AsReadOnly(); Reason = reason; Navigation = navigation; Graph = graph;
        SectionContract = contract; Bindings = bindings;
    }
}

/// <summary>
/// 玩家先在游戏选项中选择，再用“原选项＋当前实际对白”卡确认。只在克隆上验证选择和定位，
/// 不写真实历史/已听/事实，也不播放。相同对白可属于多个选项，人工卡片按原 OptionId 区分。
/// </summary>
public static class ConfirmedBranchPolicy
{
    public static ConfirmedBranchOffer Create(PlaybackEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var snapshot = engine.ExportNavigation();
        string navigation = JsonSerializer.Serialize(snapshot, Json.Options);
        string graph = PlaybackEngine.NavigationFingerprint(engine.Pack);
        string section = engine.Current?.SectionId ?? "";
        string contract = SectionFingerprint(engine.Pack, section);
        var cards = new List<ConfirmedBranchCard>();
        var bindings = new Dictionary<string, (ChoiceOption Option, Node Node)>(StringComparer.Ordinal);
        Node? menu = engine.Current;
        ConfirmedBranchOffer Finish(string reason) => new(engine, menu, section, cards, reason, navigation, graph, contract, bindings);
        if (engine.Pack.SchemaVersion != 3 || engine.Mode != RunMode.Choice || menu == null || menu.Archived ||
            menu.Kind != "choice" || engine.ReviewRoute != null || snapshot.Current.SingleLine || !engine.Allowed(menu))
            return Finish("请先到当前游戏分支，再核对原选项和选后的实际对白。");
        if (!Ordinary(menu)) return Finish("此处是人物互动、条件或复杂菜单，请用原菜单或台词目录核对。");
        var parent = BranchRouteContext.Resolve(engine, menu);
        if (parent != null ? !parent.IsConfirmedBranch || !Ordinary(parent.Menu) || parent.Parents.Any(p => !Ordinary(p.Menu)) :
            menu.PathId.Length > 0 || engine.Pack.Nodes.Where(n => !n.Archived).SelectMany(n => n.Options).Any(o => o.SegmentIds.Contains(menu.Id)))
            return Finish("当前菜单的父路线尚未确认，请先核对当前分支位置。");

        // 普通候选复用共享规则的前两句；不越过 return/merge，也不以文本像选项就自行略过。
        var ordinary = BranchAnchorPolicy.Create(engine, maximumLinesPerOption: 2);
        int unavailable = 0;
        for (int i = 0; i < menu.Options.Count; i++)
        {
            var option = menu.Options[i]; int number = i + 1, before = cards.Count;
            if (!BodyKnown(option) || !UniqueOwner(engine.Pack, menu, option)) { unavailable++; continue; }
            if (DefaultBranchDisplayPolicy.TryGetDisplayAnchor(engine, number, out var first, out var mapped) &&
                first?.Id == option.TargetId && mapped != null)
            {
                // 只有已核源映射可直接以实际首对白替代选项复述。两项同答仍生成两张原选项卡。
                if (Probe(engine, snapshot, menu, option, mapped, mapped.Id != option.TargetId))
                    Add(option, number, mapped, 1, mapped.Id != option.TargetId);
            }
            else
            {
                foreach (var card in ordinary.Cards.Where(c => c.OptionId == option.Id))
                    if (engine.Pack.ById.TryGetValue(card.NodeId, out var node) && Probe(engine, snapshot, menu, option, node, false))
                        Add(option, number, node, card.Position, node.Id != option.TargetId);
            }
            if (cards.Count == before) unavailable++;
        }
        return Finish(cards.Count == 0 ? "没有可确认的已核选后对白，请按游戏画面使用原菜单或台词目录。" :
            unavailable > 0 ? $"另有 {unavailable} 个选项尚不能提供已核对白，请用原菜单或台词目录核对。" : "");

        void Add(ChoiceOption option, int number, Node node, int position, bool skipsOptionLine)
        {
            string id = CardId(menu.Id, option.Id, node.Id);
            if (bindings.ContainsKey(id)) return;
            var preview = Preview(engine.Pack, option, node);
            cards.Add(new(id, menu.Id, option.Id, option.Label, number, node.Id, node.Speaker, node.Text,
                position, skipsOptionLine, preview?.Speaker ?? "", preview?.Text ?? ""));
            bindings[id] = (option, node);
        }
    }

    /// <summary>
    /// 成功仅解析原选项和目标对白。真实接入应静音导入经过验证的菜单选择快照后，
    /// 调用 ConfirmGameLine(node.Id) 一次；不能在真实引擎先 SelectBranch 播选项再播对白。
    /// </summary>
    public static bool TryResolve(PlaybackEngine engine, ConfirmedBranchOffer offer, string cardId,
        out ChoiceOption? option, out Node? node, out string reason)
    {
        option = null; node = null; reason = "选后对白候选已变化，请重新对照当前游戏画面。";
        if (engine == null || offer == null || !ReferenceEquals(engine, offer.Engine) || string.IsNullOrEmpty(cardId) ||
            !ReferenceEquals(engine.Current, offer.Menu) || engine.Mode != RunMode.Choice ||
            JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options) != offer.Navigation ||
            PlaybackEngine.NavigationFingerprint(engine.Pack) != offer.Graph ||
            SectionFingerprint(engine.Pack, offer.SectionId) != offer.SectionContract ||
            !offer.Bindings.TryGetValue(cardId, out var bound)) return false;
        var old = offer.Cards.SingleOrDefault(c => c.Id == cardId);
        if (old == null || !engine.Pack.ById.TryGetValue(old.NodeId, out var actual) || !ReferenceEquals(actual, bound.Node) ||
            offer.Menu?.Options.SingleOrDefault(o => o.Id == old.OptionId) is not { } chosen || !ReferenceEquals(chosen, bound.Option)) return false;
        var current = Create(engine);
        if (current.MenuId != offer.MenuId || current.SectionId != offer.SectionId || !current.Cards.Contains(old)) return false;
        option = chosen; node = actual; reason = ""; return true;
    }

    static bool Ordinary(Node menu) => menu.Kind == "choice" && menu.MenuType == "exclusive" && !menu.Archived &&
        menu.SetFacts.Count == 0 && string.IsNullOrEmpty(menu.CompleteRoute) && menu.Options.Count > 0 &&
        menu.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() == menu.Options.Count &&
        menu.Options.Select(o => o.PathId).Distinct(StringComparer.Ordinal).Count() == menu.Options.Count &&
        menu.Options.All(o => !string.IsNullOrWhiteSpace(o.Id) && !string.IsNullOrWhiteSpace(o.PathId) &&
            o.Requires.Count == 0 && o.Excludes.Count == 0 && string.IsNullOrWhiteSpace(o.ConditionNote));
    static bool BodyKnown(ChoiceOption option) => option.BodyVerified && option.BodyEvidence.Count > 0 &&
        option.BodyEvidence.All(e => !string.IsNullOrWhiteSpace(e)) && option.SegmentIds.Contains(option.TargetId);
    static bool PlainLine(Node node) => !node.Archived && node.Kind == "line" && node.Options.Count == 0 &&
        node.SetFacts.Count == 0 && string.IsNullOrEmpty(node.CompleteRoute) && !string.IsNullOrWhiteSpace(node.Text);
    static bool UniqueOwner(Pack pack, Node menu, ChoiceOption option)
    {
        var owners = pack.Nodes.Where(n => !n.Archived && n.Kind == "choice")
            .SelectMany(n => n.Options.Select(o => (Menu:n, Option:o))).Where(x => x.Option.PathId == option.PathId).ToArray();
        return owners.Length == 1 && ReferenceEquals(owners[0].Menu, menu) && ReferenceEquals(owners[0].Option, option);
    }
    static bool Probe(PlaybackEngine engine, NavigationSnapshot snapshot, Node menu, ChoiceOption option, Node node, bool mapped)
    {
        if (!BodyKnown(option) || !PlainLine(node) || node.SectionId != menu.SectionId ||
            !mapped && (node.PathId != option.PathId || !option.SegmentIds.Contains(node.Id)) ||
            mapped && (!option.ExitVerified || option.ExitEvidence.Count == 0 || option.ExitEvidence.Any(string.IsNullOrWhiteSpace))) return false;
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(snapshot)) return false;
        probe.Commit(menu.Id);
        probe.SelectBranch(probe.AvailableOptions.FindIndex(o => o.Id == option.Id));
        if (probe.CurrentId != option.TargetId || probe.Mode != RunMode.Following || probe.ReviewRoute != null ||
            probe.ExportNavigation().Current.SingleLine || !probe.ConfirmGameLine(node.Id) ||
            probe.CurrentId != node.Id || probe.Mode != RunMode.Following || probe.ReviewRoute != null ||
            probe.ExportNavigation().Current.SingleLine || !probe.Allowed(node) ||
            !probe.Choices.TryGetValue(menu.Id, out var selected) || selected != option.PathId ||
            !probe.Facts.SetEquals(snapshot.Current.Facts) || !probe.Heard.SetEquals(snapshot.Current.Heard)) return false;
        return BranchRouteContext.TryResumeAuto(probe, out _) || BranchRouteContext.IsVerifiedCommon(probe);
    }
    static Node? Preview(Pack pack, ChoiceOption option, Node current)
    {
        if (current.NextId == null || !pack.ById.TryGetValue(current.NextId, out var next) ||
            !PlainLine(next) || next.SectionId != current.SectionId || next.PathId != option.PathId ||
            current.PathId != option.PathId || !option.SegmentIds.Contains(next.Id)) return null;
        return next;
    }
    static string CardId(string menu, string option, string node) => "confirmed:" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(menu + "\0" + option + "\0" + node)));
    static string SectionFingerprint(Pack pack, string section) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pack.Nodes.Where(n => n.SectionId == section).OrderBy(n => n.Id, StringComparer.Ordinal), Json.Options))));
}
