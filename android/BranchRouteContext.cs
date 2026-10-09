namespace PgrVoice.AndroidApp;

/// <summary>从当前包和真实选择只读解析路线身份；显示身份与自动播放准入分开。</summary>
public static class BranchRouteContext
{
    public sealed record Route(Node Menu, ChoiceOption Option, int Number)
    {
        public string PathId => Option.PathId;
        public string Label => BranchName(Number);
    }

    public sealed record Identity(Node Node, Node Menu, ChoiceOption Option, int Number,
        IReadOnlyList<Route> Parents, bool Selected, bool Verified, bool IsConfirmedBranch)
    {
        public string PathId => Option.PathId;
        public string Label => BranchName(Number);
        public string PendingLabel => Label + "（待确认）";
    }

    public static string BranchName(int number) => "分支" + NumberText(number);

    static string NumberText(int number)
    {
        const string digits = "零一二三四五六七八九";
        if (number is > 0 and < 10) return digits[number].ToString();
        if (number is >= 10 and < 100)
            return (number < 20 ? "" : digits[number / 10].ToString()) + "十" +
                (number % 10 == 0 ? "" : digits[number % 10].ToString());
        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 未选择、暂停、冷恢复、目录预览仍可显示准确身份；只有当前导航、全部祖先选择和作用范围
    /// 同时吻合才标记已接入。无法证明唯一归属时返回 null，调用方不可据此称为共同线。
    /// </summary>
    public static Identity? Resolve(PlaybackEngine engine, Node? node = null)
    {
        node ??= engine.Current;
        if (node == null || node.Archived || !engine.Pack.ById.TryGetValue(node.Id, out var actual) ||
            !ReferenceEquals(actual, node)) return null;
        var index = new Index(engine.Pack, node.SectionId);
        var chain = index.Resolve(node, new(StringComparer.Ordinal));
        if (chain == null || chain.Count == 0) return null;
        bool selected = chain.All(r => engine.Choices.TryGetValue(r.Menu.Id, out var path) && path == r.PathId);
        bool verified = engine.Pack.SchemaVersion == 3;
        var anchor = node;
        foreach (var route in chain)
        {
            verified &= VerifiedScope(engine.Pack, route, anchor);
            anchor = route.Menu;
        }
        bool current = ReferenceEquals(engine.Current, node);
        bool normalNavigation = current && engine.ReviewRoute == null && engine.Mode != RunMode.Original &&
            !engine.ExportNavigation().Current.SingleLine;
        var nearest = chain[0];
        return new(node, nearest.Menu, nearest.Option, nearest.Number, chain.Skip(1).ToArray(),
            selected, verified, selected && verified && normalNavigation && engine.Allowed(node));
    }

    /// <summary>
    /// 重开只接受实际已接入的无条件普通分支正文。后续是否可走仍由原导航/出口策略另行判断，
    /// 不用下一句碰巧可达或 Allowed 本身替代祖先选择与正文作用范围证明。
    /// </summary>
    public static bool TryResumeAuto(PlaybackEngine engine, out Identity? context)
    {
        context = Resolve(engine);
        if (engine.Pack.SchemaVersion != 3 || engine.Mode != RunMode.Following ||
            engine.Current is not { Kind: "line", Archived: false } line ||
            context is not { IsConfirmedBranch: true } identity || !engine.Allowed(line) ||
            line.Options.Count != 0 || line.SetFacts.Count != 0 || !string.IsNullOrEmpty(line.CompleteRoute)) return false;
        foreach (var route in new[] { new Route(identity.Menu, identity.Option, identity.Number) }.Concat(identity.Parents))
        {
            var menu = route.Menu;
            if (menu.MenuType != "exclusive" || menu.Kind != "choice" || menu.Archived ||
                menu.SetFacts.Count != 0 || !string.IsNullOrEmpty(menu.CompleteRoute) || menu.Options.Count == 0 ||
                menu.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() != menu.Options.Count ||
                menu.Options.Select(o => o.PathId).Distinct(StringComparer.Ordinal).Count() != menu.Options.Count ||
                menu.Options.Any(o => string.IsNullOrWhiteSpace(o.Id) || string.IsNullOrWhiteSpace(o.PathId) ||
                    o.Requires.Count != 0 || o.Excludes.Count != 0 || !string.IsNullOrWhiteSpace(o.ConditionNote))) return false;
        }
        return true;
    }

    /// <summary>
    /// 只证明当前包内的共同正文或顶层汇合身份，不改变导航、不代替运行时的 Following、Allowed、
    /// SingleLine/Review、声音和前台检查。嵌套选项必须全部有完整的普通路线出口证明。
    /// </summary>
    public static bool IsVerifiedCommon(PlaybackEngine engine, Node? node = null)
    {
        node ??= engine.Current;
        if (engine.Pack.SchemaVersion != 3 || node == null || node.Archived || node.Kind is not ("line" or "merge") ||
            !engine.Pack.ById.TryGetValue(node.Id, out var actual) || !ReferenceEquals(actual, node)) return false;
        return new CommonGraph(engine.Pack, node.SectionId).Contains(node);
    }

    static bool BodyKnown(ChoiceOption option) => option.BodyVerified && option.BodyEvidence.Count > 0 &&
        option.BodyEvidence.All(e => !string.IsNullOrWhiteSpace(e));

    static bool VerifiedScope(Pack pack, Route route, Node anchor) =>
        !route.Menu.Archived && route.Menu.SectionId == anchor.SectionId &&
        BodyKnown(route.Option) && route.Option.SegmentIds.Contains(anchor.Id) &&
        route.Option.SegmentIds.Contains(route.Option.TargetId) &&
        pack.ById.TryGetValue(route.Option.TargetId, out var target) && !target.Archived &&
        target.SectionId == anchor.SectionId;

    sealed class Index
    {
        readonly Dictionary<string, List<Route>> owners = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> scopes = new(StringComparer.Ordinal);
        readonly string sectionId;

        public Index(Pack pack, string sectionId)
        {
            this.sectionId = sectionId;
            // owner 在全包要求唯一；不能因同 PathId 的另一个菜单在别处就忽略冲突。
            foreach (var menu in pack.Nodes.Where(n => !n.Archived && n.Kind == "choice"))
            for (int i = 0; i < menu.Options.Count; i++)
            {
                var option = menu.Options[i];
                if (string.IsNullOrWhiteSpace(option.PathId)) continue;
                if (!owners.TryGetValue(option.PathId, out var values)) owners[option.PathId] = values = new();
                values.Add(new(menu, option, i + 1));
                if (menu.SectionId != sectionId || !BodyKnown(option) || !option.SegmentIds.Contains(option.TargetId)) continue;
                foreach (string id in option.SegmentIds)
                {
                    // 自己的出口不属于仍在执行的子分支；尤其不能把 merge 的 incoming 子项当父身份。
                    if (id == option.MergeId || id == option.ReturnId) continue;
                    if (!scopes.TryGetValue(id, out var paths)) scopes[id] = paths = new();
                    if (!paths.Contains(option.PathId)) paths.Add(option.PathId);
                }
            }
        }

        public List<Route>? Resolve(Node node, HashSet<string> ancestors)
        {
            if (node.Archived || node.SectionId != sectionId) return null;
            var candidates = scopes.TryGetValue(node.Id, out var contained) ? new List<string>(contained) : new();
            if (!string.IsNullOrEmpty(node.PathId) && !candidates.Contains(node.PathId)) candidates.Add(node.PathId);
            if (candidates.Count == 0) return new();
            var chains = new List<List<Route>>();
            foreach (string path in candidates)
            {
                if (ancestors.Contains(path) || !owners.TryGetValue(path, out var found) || found.Count != 1 ||
                    found[0].Menu.SectionId != sectionId) return null;
                var route = found[0];
                var parent = Resolve(route.Menu, new HashSet<string>(ancestors, StringComparer.Ordinal) { path });
                if (parent == null) return null;
                chains.Add(new[] { route }.Concat(parent).ToList());
            }
            // 非空 PathId 是明确的当前层；其它 scope 只能是它的祖先。空 PathId 则需唯一最深链
            // 同时包含所有候选，不能借“取第一个”在互斥或不相关 scope 之间猜一条。
            var deepest = chains.Where(c => (node.PathId.Length == 0 || c[0].PathId == node.PathId) &&
                candidates.All(p => c.Any(r => r.PathId == p))).ToArray();
            return deepest.Length == 1 ? deepest[0] : null;
        }
    }

    sealed class CommonGraph
    {
        readonly Pack pack;
        readonly string sectionId;
        readonly Dictionary<string, Node> active;
        readonly HashSet<string> branch = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> pathOwners;
        readonly Dictionary<(string Menu, string Join), bool> proven = new();
        readonly HashSet<string> checking = new(StringComparer.Ordinal);

        public CommonGraph(Pack pack, string sectionId)
        {
            this.pack = pack; this.sectionId = sectionId;
            active = pack.Nodes.Where(n => !n.Archived && n.SectionId == sectionId).ToDictionary(n => n.Id, StringComparer.Ordinal);
            pathOwners = pack.Nodes.Where(n => !n.Archived && n.Kind == "choice").SelectMany(n => n.Options)
                .GroupBy(o => o.PathId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            branch.UnionWith(active.Values.Where(n => n.PathId.Length > 0).Select(n => n.Id));
            foreach (var menu in active.Values.Where(n => n.Kind == "choice"))
            foreach (var option in menu.Options)
            {
                // 不因未核或漏写 PathId 就把已声明/实际从选项入口可达的分支正文升为共同线。
                branch.UnionWith(option.SegmentIds); branch.UnionWith(option.LineIds);
                if (option.BoundaryId != null) branch.Add(option.BoundaryId);
                var pending = new Stack<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
                pending.Push(option.TargetId);
                while (pending.TryPop(out var id))
                {
                    if (id != option.TargetId && (id == option.MergeId || id == option.ReturnId)) continue;
                    if (!seen.Add(id) || !active.TryGetValue(id, out var node)) continue;
                    branch.Add(id);
                    if (node.NextId != null) pending.Push(node.NextId);
                    foreach (var child in node.Options) pending.Push(child.TargetId);
                }
            }
        }

        bool Unscoped(Node node) => node.PathId.Length == 0 && !branch.Contains(node.Id);
        static bool Plain(Node node) => node.SetFacts.Count == 0 && string.IsNullOrEmpty(node.CompleteRoute);
        static bool Ordinary(Node menu) => menu.Kind == "choice" && menu.MenuType == "exclusive" && Plain(menu) &&
            menu.Options.Count > 0 && menu.Options.Select(o => o.Id).Distinct(StringComparer.Ordinal).Count() == menu.Options.Count &&
            menu.Options.Select(o => o.PathId).Distinct(StringComparer.Ordinal).Count() == menu.Options.Count &&
            menu.Options.All(o => !string.IsNullOrWhiteSpace(o.Id) && !string.IsNullOrWhiteSpace(o.PathId) &&
                o.Requires.Count == 0 && o.Excludes.Count == 0 && string.IsNullOrWhiteSpace(o.ConditionNote));

        public bool Contains(Node target)
        {
            if (!Unscoped(target) || !Plain(target) || target.Options.Count != 0) return false;
            if (target.Kind == "line")
                foreach (var section in pack.Chapters.SelectMany(c => c.Sections).Where(s => s.Id == sectionId))
                    if (TraceCommon(section.StartId, target.Id)) return true;
            foreach (var menu in active.Values.Where(n => n.Kind == "choice" && Unscoped(n)))
            {
                if (!Ordinary(menu)) continue;
                string? joinId = menu.Options[0].MergeId;
                if (joinId == null || !active.TryGetValue(joinId, out var join) || join.Kind != "merge" ||
                    !Unscoped(join) || !Plain(join) || join.Options.Count != 0 || !ProveMenu(menu, joinId)) continue;
                if (target.Id == joinId) return true;
                if (target.Kind == "line" && TraceCommon(join.NextId, target.Id)) return true;
            }
            return false;
        }

        bool TraceCommon(string? id, string targetId)
        {
            bool found = false; var seen = new HashSet<string>(StringComparer.Ordinal);
            while (id != null && active.TryGetValue(id, out var node) && node.Kind == "line")
            {
                if (!seen.Add(id)) return false;
                if (!Unscoped(node) || !Plain(node) || node.Options.Count != 0) return false;
                found |= id == targetId; id = node.NextId;
            }
            return found;
        }

        bool ProveMenu(Node menu, string joinId)
        {
            var key = (menu.Id, joinId);
            if (proven.TryGetValue(key, out bool cached)) return cached;
            if (!Ordinary(menu) || !checking.Add(menu.Id)) return false;
            bool result = active.TryGetValue(joinId, out var join) && join.Kind == "merge" &&
                join.PathId == menu.PathId && Plain(join) && join.Options.Count == 0 &&
                menu.Options.All(o => ProveOption(o, joinId));
            checking.Remove(menu.Id); proven[key] = result; return result;
        }

        bool ProveOption(ChoiceOption option, string joinId)
        {
            if (!BodyKnown(option) || !option.ExitVerified || option.ExitEvidence.Count == 0 ||
                option.ExitEvidence.Any(string.IsNullOrWhiteSpace) || option.MergeId != joinId || option.ReturnId != joinId ||
                !pathOwners.TryGetValue(option.PathId, out int owners) || owners != 1 ||
                !option.SegmentIds.Contains(option.TargetId)) return false;
            var members = new HashSet<string>(option.SegmentIds, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal); string? id = option.TargetId;
            while (id != joinId)
            {
                if (id == null || !seen.Add(id) || !members.Contains(id) || !active.TryGetValue(id, out var node) ||
                    node.PathId.Length > 0 && node.PathId != option.PathId || node.SetFacts.Count != 0) return false;
                if (node.Kind == "choice")
                {
                    string? nestedJoin = node.Options.FirstOrDefault()?.MergeId;
                    if (nestedJoin == null || !ProveMenu(node, nestedJoin)) return false;
                    id = nestedJoin; continue;
                }
                if (node.Kind is not ("line" or "return" or "merge") || node.Options.Count != 0 ||
                    !string.IsNullOrEmpty(node.CompleteRoute) && (node.Kind != "return" || node.CompleteRoute != option.PathId)) return false;
                id = node.NextId;
            }
            return seen.Count > 0;
        }
    }
}
