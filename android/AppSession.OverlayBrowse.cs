using System.Globalization;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    sealed record OverlayBrowseStamp(PlaybackEngine? Engine, Node? Current, RunMode? Mode, long Epoch,
        long AudioGeneration, string Section, IReadOnlyList<MatchCandidate> Candidates, string OcrText,
        int HistoryCount, int HistoryPosition, string? BrowsedSection, bool Directory, long BookmarksVersion, bool Importing);
    sealed record OverlayBrowseAction(Func<bool> StillValid, Action Apply);
    readonly Dictionary<string, OverlayBrowseAction> overlayBrowseActions = new(StringComparer.Ordinal);
    OverlayBrowsePage? overlayBrowsePage;
    PlaybackEngine? overlayBrowseOwner;
    OverlayBrowseStamp? overlayBrowseStamp;
    string? overlayBrowseSection;
    bool overlayBrowseDirectory, overlayBrowseDirty = true, overlayBrowseRefreshing;
    int overlayBrowseChanging;
    long overlayBrowseRevision, overlayBrowseActionId, overlayBrowseBookmarksVersion;

    /// <summary>由 Invalidate 调用。撤销旧确认框的权限，但不清除用户正在查看的页。</summary>
    void ResetOverlayBrowseRequests()
    {
        overlayBrowseRevision = checked(overlayBrowseRevision + 1);
        overlayBrowseActions.Clear(); overlayBrowseStamp = null; overlayBrowseDirty = true;
    }

    void OnOverlayBrowseRefreshRequested() => Post(() =>
    {
        // 重新显露旧页只刷新模型，不改变位置、目录层级或跟随意图。
        overlayBrowseDirty = true;
        RefreshOverlayBrowseIfNeeded();
    });

    void OnOverlayBrowseRequested(OverlayBrowsePage page) => Post(() =>
    {
        if (!Enum.IsDefined(page)) return;
        var continuation = page == OverlayBrowsePage.Branches ? CaptureBranchBrowseSource() : null;
        overlayBrowseChanging++;
        try
        {
            if (Listening.IsPlaying) Listening.Pause();
            overlayBrowsePage = page; overlayBrowseDirectory = false;
            // 菜单仍保持 Choice/Gap，原声仍冻结；只暂停音频和跟随，不抹去 OCR 候选。
            Invalidate(false, preserveBranchIntent: page == OverlayBrowsePage.Branches); audio.Stop(); Engine?.PauseForBrowse();
            overlayBrowseDirty = true;
            Status = Engine == null ? "请先在应用中导入并打开一个章节。" : "已暂停跟随，可在悬浮页中核对台词、分支或记录。";
            BeginBranchBrowseContinuation(continuation);
        }
        finally { overlayBrowseChanging--; }
        RefreshOverlayBrowseIfNeeded(); Notify();
    });

    void OnOverlayBrowseSelected(long revision, string id) => Post(() =>
    {
        if (string.IsNullOrWhiteSpace(id) || revision != overlayBrowseRevision || overlayBrowseStamp == null ||
            overlayBrowseStamp != CurrentOverlayBrowseStamp() ||
            !overlayBrowseActions.TryGetValue(id, out var action) || !action.StillValid())
        {
            overlayBrowseDirty = true;
            Status = "这条悬浮操作已失效，章节、位置或候选发生了变化。请重新选择。";
            RefreshOverlayBrowseIfNeeded(); Notify(); return;
        }
        overlayBrowseChanging++;
        try
        {
            // 先消费令牌，再执行动作；重复确认、旧对话框和迟到点击均不能复用。
            ResetOverlayBrowseRequests(); action.Apply(); overlayBrowseDirty = true;
        }
        finally
        {
            overlayBrowseChanging--;
            RefreshOverlayBrowseIfNeeded(); Notify();
        }
    });

    OverlayBrowseStamp CurrentOverlayBrowseStamp() => new(Engine, Engine?.Current, Engine?.Mode, ocrEpoch,
        playGeneration, SectionId, Candidates, OcrText, Engine?.History.Count ?? 0, Engine?.HistoryPosition ?? -1,
        overlayBrowseSection, overlayBrowseDirectory, overlayBrowseBookmarksVersion, IsImporting);

    /// <summary>由 Notify 调用。没有剧情/模式/候选等变化时不重建整节列表。</summary>
    void RefreshOverlayBrowseIfNeeded()
    {
        if (overlayBrowsePage is not { } page || !overlay.IsBrowseVisible || overlayBrowseChanging > 0 || overlayBrowseRefreshing) return;
        if (!ReferenceEquals(overlayBrowseOwner, Engine))
        {
            overlayBrowseOwner = Engine; overlayBrowseSection = null; overlayBrowseDirectory = false; overlayBrowseDirty = true;
        }
        var stamp = CurrentOverlayBrowseStamp();
        if (!overlayBrowseDirty && overlayBrowseStamp == stamp) return;
        overlayBrowseRefreshing = true;
        try
        {
            overlayBrowseRevision = checked(overlayBrowseRevision + 1); overlayBrowseActions.Clear();
            var items = new List<OverlayBrowseItem>(); var actions = new List<OverlayBrowseItem>();
            string title = page switch { OverlayBrowsePage.Lines => "当前台词", OverlayBrowsePage.Locate => "定位候选", OverlayBrowsePage.Branches => "分支与菜单", _ => "书签与记录" };
            string hint = "请先在应用中导入并打开一个章节。";
            if (Engine is { } engine)
            {
                if (IsImporting) hint = "章节正在导入，请完成后再选择位置。";
                else switch (page)
                {
                    case OverlayBrowsePage.Lines: BuildOverlayLines(engine, items, actions, out title, out hint); break;
                    case OverlayBrowsePage.Locate: BuildOverlayLocate(engine, items, actions, out hint); break;
                    case OverlayBrowsePage.Branches: BuildOverlayBranches(engine, items, actions, out title, out hint); break;
                    case OverlayBrowsePage.History: BuildOverlayHistory(engine, items, actions, out hint); break;
                }
            }
            overlayBrowseStamp = CurrentOverlayBrowseStamp(); overlayBrowseDirty = false;
            overlay.UpdateBrowse(new(page, overlayBrowseRevision, title, hint, items, actions));
        }
        finally { overlayBrowseRefreshing = false; }
    }

    OverlayBrowseItem BrowseItem(string title, string detail, Action apply, Func<bool>? valid = null,
        bool selected = false, bool confirmation = true, string confirmationTitle = "确认当前位置", string confirmationText = "", bool dialogue = false)
    {
        string id = "browse:" + checked(++overlayBrowseActionId).ToString(CultureInfo.InvariantCulture);
        overlayBrowseActions.Add(id, new(valid ?? (() => true), apply));
        return new(id, title, detail, selected, confirmation, confirmationTitle,
            confirmationText.Length == 0 ? title + "\n\n" + detail : confirmationText, dialogue);
    }

    static string BrowseSpeaker(Node node) => string.IsNullOrWhiteSpace(node.Speaker) ? "旁白" : node.Speaker;
    static bool BrowseNodeValid(PlaybackEngine engine, Node node) => !node.Archived &&
        engine.Pack.ById.TryGetValue(node.Id, out var current) && ReferenceEquals(current, node);
    static string BrowseSectionTitle(PlaybackEngine engine, string? id) =>
        engine.Pack.Chapters.SelectMany(c => c.Sections).Any(s => s.Id == id)
            ? SectionDisplay.Title(engine.Pack.Chapters.SelectMany(c => c.Sections), id!) : "当前小节";
    static string BrowseExcerpt(string text, int limit) => text.Length <= limit ? text : text[..limit].TrimEnd() + "…";

    void BuildOverlayLines(PlaybackEngine engine, List<OverlayBrowseItem> items, List<OverlayBrowseItem> actions,
        out string title, out string hint)
    {
        var groups = SectionDisplay.Groups(engine.Pack.Chapters.SelectMany(c => c.Sections));
        var sections = groups.SelectMany(g => g.Sections).ToList();
        string desired = overlayBrowseSection ?? SectionId;
        int sectionIndex = sections.FindIndex(s => s.Id == desired);
        if (sectionIndex < 0) sectionIndex = sections.FindIndex(s => s.Id == engine.Current?.SectionId);
        if (sectionIndex < 0) sectionIndex = 0;
        if (sections.Count == 0) { title = "当前台词"; hint = "本章没有可浏览的小节。"; return; }
        var section = sections[sectionIndex]; overlayBrowseSection = section.Id;
        var group = groups.First(g => g.Sections.Any(s => s.Id == section.Id));
        var lines = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == section.Id).ToList();
        title = group.Title + (group.Sections.Count > 1 ? $" · 第 {group.Sections.ToList().FindIndex(s => s.Id == section.Id) + 1} 段" : "") + " · 台词";
        hint = (group.Sections.Count > 1 ? SectionDisplay.SegmentLabel(engine.Pack, group, section) + "\n" : "") + $"共 {lines.Count} 句。选句后核对游戏画面再确认；翻阅小节不会改变播放位置。";
        bool original = engine.Mode == RunMode.Original;
        foreach (var (node, index) in lines.Select((n, i) => (n, i)))
        {
            string context = engine.LocationContext(node), notice = engine.Pack.AudioNotice(node);
            items.Add(BrowseItem($"{index + 1}. {BrowseSpeaker(node)}：{node.Text}",
                context + (notice.Length == 0 ? "" : "\n" + notice), () => ConfirmLine(node.Id, original),
                () => BrowseNodeValid(engine, node), node.Id == engine.CurrentId,
                confirmationTitle: original ? "结束原声并从这里续接？" : "确认游戏当前台词",
                confirmationText: BrowseSpeaker(node) + "：" + node.Text + "\n\n" + context + "\n\n请确保与游戏当前画面一致。"));
        }
        void AddSectionAction(string label, string sectionId)
        {
            var targetGroup = groups.First(g => g.Sections.Any(s => s.Id == sectionId));
            var target = targetGroup.Sections.First(s => s.Id == sectionId);
            string preview = targetGroup.Title + (targetGroup.Sections.Count > 1 ? "\n" + SectionDisplay.SegmentLabel(engine.Pack, targetGroup, target) : "");
            actions.Add(BrowseItem(label, preview,
                () => { overlayBrowseSection = sectionId; overlayBrowseDirty = true; },
                () => engine.Pack.Chapters.SelectMany(c => c.Sections).Any(s => s.Id == sectionId), confirmation: false));
        }
        if (sectionIndex > 0) AddSectionAction(group.Sections.Contains(sections[sectionIndex - 1]) ? "上一段" : "上一小节", sections[sectionIndex - 1].Id);
        if (sectionIndex + 1 < sections.Count) AddSectionAction(group.Sections.Contains(sections[sectionIndex + 1]) ? "下一段" : "下一小节", sections[sectionIndex + 1].Id);
        if (engine.Current is { } current && current.SectionId != section.Id) AddSectionAction("回到当前小节", current.SectionId);
    }

    void BuildOverlayLocate(PlaybackEngine engine, List<OverlayBrowseItem> items, List<OverlayBrowseItem> actions, out string hint)
    {
        string current = engine.Current is { } currentNode ? BrowseSpeaker(currentNode) + "：" + currentNode.Text : "尚未选择";
        hint = "当前配音：" + BrowseExcerpt(current, 100) + "\n上次识别原文（仅供核对）：" + BrowseExcerpt(OcrText, 180) + "\n" +
            (Candidates.Count == 0 ? "没有待确认候选，可重新识别，或去台词页手动选句。" : "识别原文不代表播放位置，请核对候选和路线后确认。");
        foreach (var candidate in Candidates)
        {
            var node = candidate.Node;
            if (node.Kind is not ("line" or "choice") || !BrowseNodeValid(engine, node)) continue;
            bool original = engine.Mode == RunMode.Original;
            items.Add(BrowseItem(candidate.ToString(), candidate.Context,
                () => { if (node.Kind == "choice") Command(e => { if (!e.OpenGameMenu(node.Id, node.SectionId)) throw new InvalidOperationException(e.NavigationError); }); else ConfirmLine(node.Id, original); },
                () => BrowseNodeValid(engine, node) && Candidates.Any(c => ReferenceEquals(c, candidate)), node.Id == engine.CurrentId,
                confirmationTitle: node.Kind == "choice" ? "定位游戏中的分支菜单" : original ? "结束原声并从这里续接？" : "确认游戏当前台词",
                confirmationText: BrowseSpeaker(node) + "：" + node.Text + "\n\n" + engine.LocationContext(node) + "\n\n请确保与游戏当前画面一致。"));
        }
        if (CanReviewRecognizedText)
            actions.Add(BrowseItem("采用上次识别原文", "从保留的识别原文重新查找候选；选择后仍需核对确认。",
                () => { PrepareRecognizedTextCandidates(); }, () => CanReviewRecognizedText, confirmation: false));
        actions.Add(BrowseItem("OCR 重新定位", "重新读取游戏当前字幕；不会自动采用候选。", () => RequestOcr(true), confirmation: false));
        actions.Add(BrowseItem("定位范围："+(Settings.OcrFullScreen?"全画面":"框选区域"),
            "点击切换识别范围，不修改保存的字幕框。", () => SetOcrFullScreen(!Settings.OcrFullScreen), confirmation: false));
    }

    // 目录只是浏览状态。打开一个菜单后必须退出目录，否则选择后的选项会一直被总目录遮住。
    void OpenOverlayGameMenu(string id, string section)
    {
        Command(e =>
        {
            if (!e.OpenGameMenu(id, section)) throw new InvalidOperationException(e.NavigationError);
            overlayBrowseDirectory = false;
        });
        if (Engine is { } engine) TryBeginInteractionFromOverlay(engine);
    }

    (Node Menu, ChoiceOption Option)? CurrentOverlayBranch(PlaybackEngine engine)
    {
        if (engine.Current is not { } current) return null;
        var owners = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "choice" && n.SectionId == current.SectionId)
            .SelectMany(n => n.Options.Select(o => (Menu: n, Option: o)))
            .Where(pair => current.Kind == "line" && current.PathId.Length > 0
                ? pair.Option.PathId == current.PathId
                : pair.Option.Id == engine.ReviewRoute && !(engine.Pack.SchemaVersion == 3 ? pair.Option.BodyVerified : pair.Option.Verified))
            .ToList();
        return owners.Count == 1 ? owners[0] : null;
    }

    void BuildOverlayBranchLines(PlaybackEngine engine, ChoiceOption option, List<OverlayBrowseItem> items,
        List<OverlayBrowseItem> actions, out string hint)
    {
        bool verified = engine.Pack.SchemaVersion == 3 ? option.BodyVerified : option.Verified;
        hint = verified && Settings.AutoPlayConfirmBranch
            ? "这里显示当前已选路线的台词。可核对当前位置后开启自动播放，后续遇到选项再确认对白。"
            : verified
            ? "这里是所选支线的台词。游戏显示下一句后，点“下一句”推进配音；也可在控制页开启点按跟随。支线不自动连播。"
            : "这段台词的先后连接尚未核对。请按游戏画面点选对应句，确认后只播放这一句。";
        var lines = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == engine.Current!.SectionId &&
            n.PathId == option.PathId && (verified
                ? engine.Pack.SchemaVersion < 3 || option.SegmentIds.Contains(n.Id)
                : option.LineIds.Contains(n.Id))).ToList();
        if (engine.Current is { Kind: "line" } current)
        {
            if (!lines.Contains(current)) lines.Add(current);
            actions.Add(BrowseItem("下一句", "仅推进配音；先在游戏里点到下一句。到选项或段尾会停下。",
                () => ManualOverlayStep(e => e.Next(true)), () => ReferenceEquals(engine.Current, current), confirmation: false));
            actions.Add(BrowseItem("重播当前句", "重新播放当前台词，不点击游戏。",
                () => ManualOverlayStep(e => e.Replay()), () => ReferenceEquals(engine.Current, current), confirmation: false));
        }
        foreach (var (node, index) in lines.Select((n, i) => (n, i)))
        {
            string context = engine.LocationContext(node), notice = engine.Pack.AudioNotice(node);
            items.Add(BrowseItem($"{index + 1}. {BrowseSpeaker(node)}：{node.Text}",
                context + (notice.Length == 0 ? "" : "\n" + notice), () => ConfirmLine(node.Id),
                () => BrowseNodeValid(engine, node), node.Id == engine.CurrentId,
                confirmationTitle: "确认游戏当前台词", confirmationText: BrowseSpeaker(node) + "：" + node.Text +
                "\n\n" + context + "\n\n请确保与游戏当前画面一致。", dialogue: true));
        }
    }

    void BuildOverlayBranches(PlaybackEngine engine, List<OverlayBrowseItem> items, List<OverlayBrowseItem> actions,
        out string title, out string hint)
    {
        title = "分支与菜单";
        if (engine.Mode == RunMode.Original)
        { hint = "原声时段保持位置冻结。先到台词或定位页选择续接句，再操作分支。"; return; }
        var branch = CurrentOverlayBranch(engine);
        hint = "在这里选择与游戏相同的选项，确认后播放对应台词；不会替你点击游戏选项。";
        if (!overlayBrowseDirectory && branch is { } route)
        {
            title = "当前支线 · " + route.Option.Label;
            BuildOverlayBranchLines(engine, route.Option, items, actions, out hint);
        }
        else if (!overlayBrowseDirectory && engine.Mode == RunMode.Choice)
        {
            if (Settings.AutoPlayConfirmBranch && ConfirmedBranchPolicy.Create(engine).Cards.Count > 0)
                actions.Add(BrowseItem("按选后对白确认并继续自动播放", "先在游戏选好，再点对应的选项与对白卡片。", StartAutoPlayback, confirmation: false));
            if (branchBrowseContinuation is { } continuation && BranchBrowseContinuationValid(continuation))
                hint = continuation.Source.Intent.DefaultOption > 0 ? "先在游戏中选择默认分支，再确认同项；确认后自动续播，不必再次开启。" :
                    "已到分支，请先在游戏中选择，再确认并开启点按跟随。无需再到控制页开启。";
            else if (branchBrowseStopped is { } stopped && ReferenceEquals(stopped.Engine, engine) && ReferenceEquals(stopped.Menu, engine.Current))
                hint = stopped.Reason + " 下面的确认只播放所选分支，不会自动重开跟随。";
            actions.Add(BrowseItem("游戏选好后，识别下一句", "只识别本次完整对白，请核对台词与路线；未核连接仍等待。", RequestBranchAnchorOcr, confirmation: false));
            foreach (var option in engine.AvailableOptions)
            {
                string displayDescription = DefaultBranchBrowseDisplayDescription(engine, option);
                items.Add(BrowseItem(engine.Current?.MenuType is "interaction" or "topics" ?
                    BranchFollowPolicy.InteractionOptionLabel(engine, engine.Current, option) : option.ToString(),
                    string.IsNullOrEmpty(displayDescription) ? option.Reason : displayDescription,
                    () => SelectOverlayBranch(engine, option),
                    () => engine.Mode == RunMode.Choice && engine.AvailableOptions.Any(o => ReferenceEquals(o, option)),
                    confirmationTitle: branchBrowseContinuation == null ? "确认与游戏相同的分支" : "确认分支并继续跟随", confirmationText: option.Label + "\n\n" + option.Reason +
                        (branchBrowseContinuation == null ? "" : "\n\n请先在游戏中选好；确认后直接恢复本次跟随。") +
                        (string.IsNullOrEmpty(displayDescription) ? "" : "\n\n" + displayDescription)));
            }
        }
        else if (!overlayBrowseDirectory && engine.Mode == RunMode.Gap && engine.ResumeMenus.Count > 0)
        {
            hint = "当前段落已到待续接边界，请核对游戏中的菜单。";
            foreach (var menu in engine.ResumeMenus)
                items.Add(BrowseItem(menu.Text, engine.LocationContext(menu),
                    () => Command(e => { int i = e.ResumeMenus.FindIndex(m => ReferenceEquals(m, menu)); if (i < 0) throw new InvalidOperationException("续接菜单已变化。"); overlayBrowseDirectory = false; e.SelectContinuation(i); }),
                    () => engine.Mode == RunMode.Gap && BrowseNodeValid(engine, menu) && engine.ResumeMenus.Any(m => ReferenceEquals(m, menu)),
                    confirmationTitle: "确认游戏当前的续接菜单"));
        }
        else
        {
            string section = engine.Pack.Chapters.SelectMany(c => c.Sections).Any(s => s.Id == SectionId) ? SectionId : engine.Current?.SectionId ?? "";
            hint = "按游戏画面直接选择人物、话题或分支菜单，无需重读前置。打开菜单保持静音，再选择具体选项。";
            foreach (var target in engine.GetStoryMenus(section))
            {
                if (!engine.Pack.ById.TryGetValue(target.MenuId, out var node)) continue;
                items.Add(BrowseItem(target.Label, target.Preview,
                    () => OpenOverlayGameMenu(target.MenuId, section),
                    () => BrowseNodeValid(engine, node) && node.Kind == "choice", target.IsCurrent,
                    confirmationTitle: "定位游戏当前菜单"));
            }
        }
        if (engine.ParentStoryMenu is { } parent)
            actions.Add(BrowseItem("返回上级菜单", "按游戏画面返回所属菜单，保持静音。",
                () => OpenOverlayGameMenu(parent.MenuId, engine.Current!.SectionId), confirmationTitle: "确认返回上级菜单"));
        if (!overlayBrowseDirectory && (branch != null || engine.Mode is RunMode.Choice or RunMode.Gap))
            actions.Add(BrowseItem("人物、话题与分支目录", "浏览当前小节的所有选择点。",
                () => { overlayBrowseDirectory = true; overlayBrowseDirty = true; }, confirmation: false));
        else if (overlayBrowseDirectory && (branch != null || engine.Mode is RunMode.Choice or RunMode.Gap))
            actions.Add(BrowseItem(branch != null ? "回到当前支线" : "回到当前分支", "显示当前支线台词、选项或续接菜单。",
                () => { overlayBrowseDirectory = false; overlayBrowseDirty = true; }, confirmation: false));
        if (engine.RecentChoices.Count > 0)
            actions.Add(BrowseItem("重选最近一次分支", "恢复最近一次实际选择前的位置，不代替游戏进行选择。",
                () => { Command(e => { if (!e.ReselectLastChoice()) throw new InvalidOperationException(e.NavigationError); overlayBrowseDirectory = false; });
                    if (Settings.AutoPlayConfirmBranch && Engine?.Mode == RunMode.Choice) StartAutoPlayback(); }, confirmationTitle: "重新选择最近分支"));
    }

    void BuildOverlayHistory(PlaybackEngine engine, List<OverlayBrowseItem> items, List<OverlayBrowseItem> actions, out string hint)
    {
        hint = engine.Mode == RunMode.Original ? "原声时段不能恢复书签或履历；请先选择续接台词。" : "显示本章书签和最近 40 条履历。恢复保持静音，游戏画面不会随之回退。";
        foreach (var bookmark in Progress.Bookmarks(engine.Pack.Id))
        {
            bool SameBookmark() => Progress.Bookmarks(engine.Pack.Id).Any(b => b.Id == bookmark.Id &&
                JsonSerializer.Serialize(b, Json.Options) == JsonSerializer.Serialize(bookmark, Json.Options));
            items.Add(BrowseItem("★ " + bookmark.Label, bookmark.SectionTitle + "\n" + bookmark.Text,
                () => Command(e => { if (!e.RestoreBookmark(bookmark)) throw new InvalidOperationException(e.NavigationError); }),
                () => engine.Mode != RunMode.Original && SameBookmark(), confirmationTitle: "静音恢复书签",
                confirmationText: bookmark.Label + "\n\n" + bookmark.Text + "\n\n只恢复配音位置，不会回退游戏。"));
        }
        for (int index = engine.History.Count - 1; index >= Math.Max(0, engine.History.Count - 40); index--)
        {
            int visitIndex = index; var visit = engine.History[index];
            if (!engine.Pack.ById.TryGetValue(visit.NodeId, out var node)) continue;
            items.Add(BrowseItem($"记录 {index + 1} · {BrowseSpeaker(node)}：{node.Text}", engine.LocationContext(node),
                () => Command(e => { if (!e.RestoreVisit(visitIndex, false)) throw new InvalidOperationException(e.NavigationError); }),
                () => engine.Mode != RunMode.Original && visitIndex < engine.History.Count && ReferenceEquals(engine.History[visitIndex], visit),
                visitIndex == engine.HistoryPosition, confirmationTitle: "静音恢复台词记录",
                confirmationText: BrowseSpeaker(node) + "：" + node.Text + "\n\n只恢复配音位置，不会回退游戏。"));
        }
        if (engine.Current is { } current)
        {
            string location = BrowseSectionTitle(engine, current.SectionId);
            int lineNumber = engine.Pack.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == current.SectionId).ToList().FindIndex(n => n.Id == current.Id) + 1;
            string label = BrowseSpeaker(current) + " · " + location + (lineNumber > 0 ? $" · 第 {lineNumber} 句" : " · 菜单位置");
            actions.Add(BrowseItem("保存当前书签", label, () =>
            {
                Progress.SaveBookmark(engine.Pack.Id, engine.CreateBookmark(label)); overlayBrowseBookmarksVersion++;
                Status = "书签已保存：" + label; overlayBrowseDirty = true;
            }, () => ReferenceEquals(engine.Current, current), confirmation: false));
        }
    }
}
