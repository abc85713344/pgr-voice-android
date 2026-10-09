using PgrVoice;
using PgrVoice.AndroidApp;

int passed = 0;
void Check(string name, Action check) { check(); Console.WriteLine("PASS " + name); passed++; }
void Require(bool condition, string message = "检查未通过") { if (!condition) throw new Exception(message); }
OcrBlock Block(string text, double left = 600, double top = 200, double right = 1000, double bottom = 250, double score = .99) =>
    new() { Text = text, Score = score, Box = [[left, top], [right, top], [right, bottom], [left, bottom]] };
List<OcrBlock> MenuFrame() => [Block("先了解营地情况。"), Block("直接出发寻找队员。", top: 350, bottom: 400)];
PlaybackEngine Fixture()
{
    var pack = new Pack
    {
        SchemaVersion = 3, Id = "branch-follow-fixture", Root = Path.GetTempPath(),
        Chapters = [new() { Id = "c", Sections = [new() { Id = "s", StartId = "menu" }] }],
        Nodes = [
            new() { Id = "menu", SectionId = "s", Kind = "choice", MenuType = "exclusive", Options = [
                new() { Id = "a", Label = "先了解营地情况。", TargetId = "a1", PathId = "pa", MergeId = "merge", ReturnId = "merge",
                    BodyVerified = true, ExitVerified = true, BodyEvidence = ["fixture-body"], ExitEvidence = ["fixture-exit"], SegmentIds = ["a1", "ar"] },
                new() { Id = "b", Label = "直接出发寻找队员。", TargetId = "b1", PathId = "pb", MergeId = "merge", ReturnId = "merge",
                    BodyVerified = true, ExitVerified = true, BodyEvidence = ["fixture-body"], ExitEvidence = ["fixture-exit"], SegmentIds = ["b1", "br"] }] },
            new() { Id = "a1", SectionId = "s", PathId = "pa", Text = "营地里还有很多人需要帮助。", Speaker = "露西亚", NextId = "ar" },
            new() { Id = "b1", SectionId = "s", PathId = "pb", Text = "我们的队员正在前面的路口等待。", Speaker = "丽芙", NextId = "br" },
            new() { Id = "ar", SectionId = "s", PathId = "pa", Kind = "return", NextId = "merge", CompleteRoute = "pa" },
            new() { Id = "br", SectionId = "s", PathId = "pb", Kind = "return", NextId = "merge", CompleteRoute = "pb" },
            new() { Id = "merge", SectionId = "s", Kind = "merge", NextId = "common" },
            new() { Id = "common", SectionId = "s", Text = "继续向前走吧。" }
        ]
    };
    pack.Validate(); var engine = new PlaybackEngine(pack); Require(engine.OpenGameMenu("menu", "s")); return engine;
}
bool Menu(PlaybackEngine engine, IReadOnlyList<OcrBlock> blocks, out BranchFollowMenuMatch? match) =>
    BranchFollowPolicy.TryMatchMenu(engine, "menu", blocks, 1200, 800, out match, out _);
bool Line(PlaybackEngine engine, string option, IReadOnlyList<OcrBlock> blocks, out string? id) =>
    BranchFollowPolicy.TryMatchSelectedLine(engine, "menu", option, blocks, 1200, 800, out id, out _);

Check("完整二选一各有独立命中框，识别不改位置/历史", () =>
{
    var engine = Fixture(); int history = engine.History.Count;
    Require(Menu(engine, MenuFrame(), out var match));
    Require(match!.Options[0].OptionId == "a" && match.Options[1].OptionId == "b");
    Require(match.Options[0].Bounds.Contains(700, 220) && !match.Options[0].Bounds.Contains(700, 370));
    Require(engine.CurrentId == "menu" && engine.History.Count == history && engine.Choices.Count == 0);
});
Check("两个新画面的稳定位置比较", () =>
{
    var engine = Fixture(); Menu(engine, MenuFrame(), out var first);
    var moved = MenuFrame(); moved[0] = Block(moved[0].Text, 605, 203, 1005, 253); Menu(engine, moved, out var second);
    Require(BranchFollowPolicy.AreSameMenu(first!, second!));
    moved[0] = Block(moved[0].Text, 640, 230, 1040, 280); Menu(engine, moved, out var third);
    Require(!BranchFollowPolicy.AreSameMenu(first!, third!));
});
Check("半句、模糊近音、低置信度均不点击", () =>
{
    var engine = Fixture();
    foreach (string wrong in new[] { "先了解营地", "先了解营地清况。" })
    { var blocks = MenuFrame(); blocks[0].Text = wrong; Require(!Menu(engine, blocks, out _)); }
    var weak = MenuFrame(); weak[0].Score = .5; Require(!Menu(engine, weak, out _));
});
Check("重复选项文字、框重叠、单框含两个选项均拒绝", () =>
{
    var engine = Fixture(); var duplicate = MenuFrame(); duplicate.Add(Block(duplicate[0].Text, top: 500, bottom: 550));
    Require(!Menu(engine, duplicate, out _));
    var overlap = MenuFrame(); overlap[1].Box = overlap[0].Box; Require(!Menu(engine, overlap, out _));
    Require(!Menu(engine, [Block("先了解营地情况。直接出发寻找队员。")], out _));
});
Check("可相邻拼接完整选项，远处文字不拼成选项", () =>
{
    var engine = Fixture();
    var split = new List<OcrBlock> { Block("先了解", 600, 200, 690, 240), Block("营地情况。", 700, 200, 850, 240), MenuFrame()[1] };
    Require(Menu(engine, split, out _));
    split[1] = Block("营地情况。", 1050, 200, 1190, 240); Require(!Menu(engine, split, out _));
});
Check("越界/NaN/畸形框不产生可点击位置", () =>
{
    var engine = Fixture();
    foreach (var invalid in new[] { Block(MenuFrame()[0].Text, -1), Block(MenuFrame()[0].Text, right: 1201), Block(MenuFrame()[0].Text, double.NaN) })
    { var blocks = MenuFrame(); blocks[0] = invalid; Require(!Menu(engine, blocks, out _)); }
    var malformed = MenuFrame(); malformed[0].Box = [[1, 2], [3, 4]]; Require(!Menu(engine, malformed, out _));
});
Check("已知裁剪缩放映回屏幕，未知/越界参数拒绝", () =>
{
    Require(BranchFollowPolicy.TryMapToScreen(new(100, 50, 200, 100), 400, 200, new(200, 100, 1000, 500), 1200, 800, out var mapped));
    Require(mapped == new BranchFollowRect(400, 200, 600, 300));
    Require(!BranchFollowPolicy.TryMapToScreen(new(-1, 50, 200, 100), 400, 200, new(200, 100, 1000, 500), 1200, 800, out _));
    Require(!BranchFollowPolicy.TryMapToScreen(new(100, 50, 200, 100), 0, 200, new(200, 100, 1000, 500), 1200, 800, out _));
});
Check("互动/话题/条件事实/未核正文/缺失证据拒绝", () =>
{
    foreach (string kind in new[] { "interaction", "topics" })
    { var engine = Fixture(); engine.Current!.MenuType = kind; Require(!Menu(engine, MenuFrame(), out _)); }
    var conditional = Fixture(); conditional.Current!.Options[0].Requires.Add("completed"); Require(!Menu(conditional, MenuFrame(), out _));
    var excluded = Fixture(); excluded.Current!.Options[0].Excludes.Add("completed"); Require(!Menu(excluded, MenuFrame(), out _));
    var fact = Fixture(); fact.Pack.ById["a1"].SetFacts.Add("completed"); Require(!Menu(fact, MenuFrame(), out _));
    var unknown = Fixture(); unknown.Current!.Options[0].BodyVerified = false; Require(!Menu(unknown, MenuFrame(), out _));
    var noEvidence = Fixture(); noEvidence.Current!.Options[0].BodyEvidence.Clear(); Require(!Menu(noEvidence, MenuFrame(), out _));
});
Check("直接正文循环、越段和无正文入口拒绝", () =>
{
    var loop = Fixture(); loop.Pack.ById["a1"].NextId = "a1"; Require(!Menu(loop, MenuFrame(), out _));
    var escaped = Fixture(); escaped.Pack.ById["a1"].NextId = "b1"; Require(!Menu(escaped, MenuFrame(), out _));
    var nested = Fixture(); nested.Pack.ById["a1"].Kind = "choice"; Require(!Menu(nested, MenuFrame(), out _));
});
Check("未知出口不会否定已核正文，第一次边界后的旧清单不用于确认", () =>
{
    var engine = Fixture(); var option = engine.Current!.Options[0]; option.ExitVerified = false;
    option.LineIds.Add("common"); Require(Menu(engine, MenuFrame(), out _));
    Require(Line(engine, "a", [Block(engine.Pack.ById["a1"].Text)], out _));
    Require(!Line(engine, "a", [Block(engine.Pack.ById["common"].Text)], out _));
});
Check("实际已核正文唯一时返回目标但不提交播放器", () =>
{
    var engine = Fixture(); Require(Line(engine, "a", [Block(engine.Pack.ById["a1"].Text)], out var id));
    Require(id == "a1" && engine.CurrentId == "menu" && engine.Choices.Count == 0);
    Require(!Line(engine, "b", [Block(engine.Pack.ById["a1"].Text)], out _));
});
Check("共享同文、短句、共同线、半句不能证明选中路线", () =>
{
    var shared = Fixture(); shared.Pack.ById["b1"].Text = shared.Pack.ById["a1"].Text;
    Require(!Line(shared, "a", [Block(shared.Pack.ById["a1"].Text)], out _));
    var shortLine = Fixture(); shortLine.Pack.ById["a1"].Text = "谢谢。"; Require(!Line(shortLine, "a", [Block("谢谢。")], out _));
    var engine = Fixture(); Require(!Line(engine, "a", [Block(engine.Pack.ById["common"].Text)], out _));
    Require(!Line(engine, "a", [Block("营地里还有很多人")], out _));
});
Check("菜单残影不当字幕，同选项台词还须实际说话人", () =>
{
    var engine = Fixture(); engine.Pack.ById["a1"].Text = engine.Current!.Options[0].Label;
    Require(!Line(engine, "a", MenuFrame(), out _));
    Require(!Line(engine, "a", [MenuFrame()[0]], out _));
    Require(Line(engine, "a", [MenuFrame()[0], Block("露西亚", 20, 650, 150, 690)], out var id) && id == "a1");
});
Check("两个不同正文同时可见或重复字幕不自动取首条", () =>
{
    var engine = Fixture(); engine.Pack.ById["ar"].Kind = "line"; engine.Pack.ById["ar"].Text = "下一句现在仍然不应被提前播放。"; engine.Pack.ById["ar"].CompleteRoute = null;
    Require(!Line(engine, "a", [Block(engine.Pack.ById["a1"].Text), Block(engine.Pack.ById["ar"].Text, top: 500, bottom: 550)], out _));
    Require(!Line(engine, "a", [Block(engine.Pack.ById["a1"].Text), Block(engine.Pack.ById["a1"].Text, top: 500, bottom: 550)], out _));
    Require(!Line(engine, "a", [Block(engine.Pack.ById["a1"].Text), Block(engine.Pack.ById["b1"].Text, top: 500, bottom: 550)], out _));
});
Check("迟到结果在原声/另位置后失效", () =>
{
    var original = Fixture(); original.EnterOriginal(); Require(!Menu(original, MenuFrame(), out _));
    var moved = Fixture(); moved.Commit("common"); Require(!Line(moved, "a", [Block(moved.Pack.ById["a1"].Text)], out _));
});

string actualPath = args.FirstOrDefault() ?? "<数据目录>/全部配音/01_主线/第32章_遥行循星/pack.json";
Require(File.Exists(actualPath), "真实32章样本不存在，请以首参数提供pack.json路径");
var actual = Json.Read<Pack>(actualPath); actual.Root = Path.GetDirectoryName(Path.GetFullPath(actualPath))!; actual.Validate();
Check("真实32章普通二选一可按实际文字确认", () =>
{
    const string menuId = "ch32-782b137489a669098935-menu-010";
    var engine = new PlaybackEngine(actual); var menu = actual.ById[menuId]; Require(engine.OpenGameMenu(menuId, menu.SectionId));
    Require(BranchFollowPolicy.IsEligibleMenu(engine, menuId, out var reason), reason);
    var blocks = new[] { Block(menu.Options[0].Label), Block(menu.Options[1].Label, top: 400, bottom: 450) };
    Require(BranchFollowPolicy.TryMatchMenu(engine, menuId, blocks, 1200, 800, out var match, out reason), reason);
    Require(match!.Options[0].OptionId == menu.Options[0].Id);
    var line = actual.ById[menu.Options[1].TargetId];
    Require(BranchFollowPolicy.TryMatchSelectedLine(engine, menuId, menu.Options[1].Id,
        [Block(line.Text), Block(line.Speaker, 20, 650, 300, 690)], 1200, 800, out var id, out reason), reason);
    Require(id == line.Id);
});
Check("真实32-6未知出口允许已核入口，22条旧清单不能越过嵌套", () =>
{
    const string menuId = "ch32-71fcf055b68c2cfb1350-menu-004";
    var engine = new PlaybackEngine(actual); var menu = actual.ById[menuId]; Require(engine.OpenGameMenu(menuId, menu.SectionId));
    Require(menu.Options[1].LineIds.Count == 22 && !menu.Options[1].ExitVerified);
    Require(BranchFollowPolicy.IsEligibleMenu(engine, menuId, out var reason), reason);
    Require(BranchFollowPolicy.TryMatchMenu(engine, menuId, [Block("进行链接。"), Block("再等等。", top: 400, bottom: 450)], 1200, 800, out _, out reason), reason);
    var secondLine = actual.ById[actual.ById[menu.Options[1].TargetId].NextId!];
    Require(BranchFollowPolicy.TryMatchSelectedLine(engine, menuId, menu.Options[1].Id, [Block(secondLine.Text)], 1200, 800, out var id, out reason), reason);
    Require(id == secondLine.Id);
    var beyond = actual.ById["ch32-71fcf055b68c2cfb1350-0b9c052ff12cde25a99c"];
    Require(!BranchFollowPolicy.TryMatchSelectedLine(engine, menuId, menu.Options[1].Id, [Block(beyond.Text)], 1200, 800, out _, out _));
});

BranchFollowAttemptGate ArmedGate()
{
    var gate = new BranchFollowAttemptGate(); gate.Start(1000);
    Require(gate.TryBeginObservation(1000, 1, 1000, false, out var ticket));
    Require(gate.CompleteObservation(ticket, 1050)); Require(gate.Arm(1050)); return gate;
}
Check("生产门禁只允许新鲜帧和一个在途识别", () =>
{
    var gate = new BranchFollowAttemptGate(); gate.Start(1000);
    Require(!gate.TryBeginObservation(1100, 1, 999, false, out _));
    Require(!gate.TryBeginObservation(1100, 1, 1101, false, out _));
    Require(!gate.TryBeginObservation(1100, 1, 1100, true, out _));
    Require(gate.TryBeginObservation(1100, 1, 1100, false, out var ticket));
    Require(!gate.TryBeginObservation(1700, 2, 1700, false, out _));
    Require(!gate.Arm(1700));
    Require(gate.CompleteObservation(ticket, 1800)); Require(!gate.CompleteObservation(ticket, 1801));
    Require(!gate.TryBeginObservation(1900, 1, 1900, false, out _));
    Require(gate.TryBeginObservation(1900, 2, 1900, false, out _));
});
Check("生产门禁拒绝过旧源图而非仅看识别请求时间", () =>
{
    var gate = new BranchFollowAttemptGate(); gate.Start(1000);
    Require(!gate.TryBeginObservation(3000, 1, 1499, false, out _));
    Require(gate.TryBeginObservation(3000, 2, 3000, false, out var ticket));
    Require(gate.CompleteObservation(ticket, 3010));
});
Check("旧识别票据跨停止和重启不能完成新识别", () =>
{
    var gate = new BranchFollowAttemptGate(); gate.Start(1000);
    Require(gate.TryBeginObservation(1000, 1, 1000, false, out var old));
    gate.Stop(); gate.Start(2000);
    Require(gate.TryBeginObservation(2000, 2, 2000, false, out var current));
    Require(!gate.CompleteObservation(old, 2050)); Require(gate.CompleteObservation(current, 2050));
});
Check("一次武装只接纳一次触摸，成功后等点选之后的新字幕帧", () =>
{
    var gate = ArmedGate(); Require(gate.TryBeginTap(1100)); long tapGeneration = gate.Generation;
    Require(!gate.TryBeginTap(1101)); Require(gate.CompleteTap(tapGeneration, true, 1150));
    Require(gate.Stage == BranchFollowStage.ReadingLine && !gate.CompleteTap(tapGeneration, true, 1160));
    Require(!gate.TryBeginObservation(1200, 2, 1149, false, out _));
    Require(gate.TryBeginObservation(1200, 3, 1200, false, out var ticket));
    Require(gate.CompleteObservation(ticket, 1250));
});
Check("手势失败停止，重复完成不能恢复", () =>
{
    var gate = ArmedGate(); Require(gate.TryBeginTap(1100)); long generation = gate.Generation;
    Require(!gate.CompleteTap(generation, false, 1150)); Require(!gate.IsActive);
    Require(!gate.CompleteTap(generation, true, 1200));
});
Check("旧手势成功回调不能完成后来新会话的手势", () =>
{
    var gate = ArmedGate(); Require(gate.TryBeginTap(1100)); long oldGeneration = gate.Generation;
    gate.Stop(); gate.Start(2000);
    Require(gate.TryBeginObservation(2000, 2, 2000, false, out var ticket));
    Require(gate.CompleteObservation(ticket, 2050)); Require(gate.Arm(2050)); Require(gate.TryBeginTap(2100));
    long newGeneration = gate.Generation;
    Require(!gate.CompleteTap(oldGeneration, true, 2150)); Require(gate.Stage == BranchFollowStage.Dispatching);
    Require(gate.CompleteTap(newGeneration, true, 2150)); Require(gate.Stage == BranchFollowStage.ReadingLine);
});
Check("识别、待点与手势超时不能推进", () =>
{
    var reading = new BranchFollowAttemptGate(); reading.Start(1000);
    Require(reading.TryBeginObservation(1000, 1, 1000, false, out var ticket));
    Require(!reading.CompleteObservation(ticket, reading.Deadline));
    var waiting = ArmedGate(); Require(!waiting.TryBeginTap(waiting.Deadline));
    var tapping = ArmedGate(); Require(tapping.TryBeginTap(1100));
    Require(!tapping.CompleteTap(tapping.Generation, true, tapping.Deadline));
});
Check("停止后所有阶段动作均失效", () =>
{
    var gate = ArmedGate(); long old = gate.Generation; gate.Stop();
    Require(!gate.Arm(1100) && !gate.TryBeginTap(1100) && !gate.CompleteTap(old, true, 1100));
    Require(!gate.TryBeginObservation(1100, 2, 1100, false, out _) && !gate.IsActive);
});

Console.WriteLine($"Branch follow policy and production gate checks: {passed} passed.");
