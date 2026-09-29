using System.Text.Json;
using PgrVoice;
using PgrVoice.Following;
using PgrVoice.AndroidApp.Ocr;

var tests = new List<object>(); int failures = 0;
void Test(string name, Action action)
{
    try { action(); tests.Add(new { name, passed = true }); }
    catch (Exception ex) { failures++; tests.Add(new { name, passed = false, error = ex.ToString() }); }
}
byte[] real = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "02-game-inner.mask"));
byte[] resumed = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "06-resume.mask"));

Test("切区首帧清空旧共识，保留armed且新区须连续两次才播放", () =>
{
    var (engine, follow) = Fixture(); int plays = 0; engine.PlayRequested += _ => plays++;
    var old = Observation(follow, 1, "region:0/next");
    Check(follow.Evaluate(engine, old).Kind == FollowDecisionKind.Waiting && follow.HasPendingConfirmation, "未建立旧区域的一次识别");
    long epoch = follow.Epoch; var stability = new SubtitleStability(); stability.Observe(real); stability.Reset();
    var changed = stability.Observe(real);
    Check(changed.Changed && !changed.Stable, "即使相同掩码，切区首帧也必须 Changed");
    var transition = Observation(follow, 2, "region:1/" + changed.FrameKey) with { IsStable = false, Blocks = [] };
    Check(follow.Evaluate(engine, transition).Kind == FollowDecisionKind.Waiting, "切区失稳观察未等待");
    Check(follow.IsArmed && follow.Epoch == epoch && !follow.HasPendingConfirmation && plays == 0, "切区解除armed、改变epoch或保留旧共识");
    Check(!stability.Observe(real).Stable && stability.Observe(real).Stable, "新区没有重新等待稳定");
    var first = Observation(follow, 3, "region:1/" + changed.FrameKey);
    Check(follow.Evaluate(engine, first).Kind == FollowDecisionKind.Waiting && engine.CurrentId == "first", "旧一次加新区一次错误推进");
    var second = first with { Sequence = 4 }; var decision = follow.Evaluate(engine, second);
    Check(decision.Kind == FollowDecisionKind.Advance && follow.TryApply(engine, second, decision), "新区两次不能推进");
    Check(engine.CurrentId == "second" && plays == 1 && !follow.TryApply(engine, second, decision), "推进重复播放");
});
Test("切区后旧区域已经生成的Advance决策不能迟到应用", () =>
{
    var (engine, follow) = Fixture(); follow.Evaluate(engine, Observation(follow, 1, "region:0/next"));
    var old = Observation(follow, 2, "region:0/next"); var oldDecision = follow.Evaluate(engine, old);
    Check(oldDecision.Kind == FollowDecisionKind.Advance, "旧决策未建立");
    follow.Evaluate(engine, Observation(follow, 3, "region:1/new") with { IsStable = false, Blocks = [] });
    Check(!follow.TryApply(engine, old, oldDecision) && engine.CurrentId == "first" && follow.IsArmed, "旧决策越过清空共识");
    Check(follow.Evaluate(engine, old).Kind == FollowDecisionKind.Ignored, "旧序号再次返回被计入");
});
Test("切回用户框时同样清空中央区的一次识别", () =>
{
    var (engine, follow) = Fixture();
    follow.Evaluate(engine, Observation(follow, 1, "region:1/center"));
    follow.Evaluate(engine, Observation(follow, 2, "region:2/user") with { IsStable = false, Blocks = [] });
    Check(!follow.HasPendingConfirmation && follow.IsArmed, "切回用户框没有清零");
    Check(follow.Evaluate(engine, Observation(follow, 3, "region:2/user")).Kind == FollowDecisionKind.Waiting, "回切只识别一次就推进");
});
Test("切区不能替用户重新开启已暂停的跟随", () =>
{
    var (engine, follow) = Fixture(); follow.Invalidate();
    var changed = Observation(follow, 1, "region:1/center") with { IsStable = false, Blocks = [] };
    Check(follow.Evaluate(engine, changed).Kind == FollowDecisionKind.Ignored && !follow.IsArmed, "区变更自动armed");
    Check(follow.Evaluate(engine, Observation(follow, 2, "region:1/center")).Kind == FollowDecisionKind.Ignored && engine.CurrentId == "first", "暂停时新区识别启动播放");
});
Test("切区清空OCR调度但旧推理占位保留，旧完成不算新区一次", () =>
{
    var schedule = new OcrInferenceSchedule();
    Check(schedule.TryBegin(0, 8, "region:0", true, false, false, out var old), "旧任务未启动");
    schedule.Reset();
    Check(!schedule.TryBegin(500, 8, "region:1", true, false, false, out _), "旧任务未退槽就并发新区推理");
    Check(!schedule.Complete(old, true, false, 10), "旧任务完成被算入新区");
    Check(schedule.TryBegin(500, 8, "region:1", true, false, false, out var first), "旧任务退槽后无法启动");
    Check(schedule.Complete(first, true, false, 10), "新区第一次失败");
    Check(schedule.TryBegin(1000, 8, "region:1", true, false, false, out var second), "旧任务导致新区第一次后提前休眠");
    Check(schedule.Complete(second, true, false, 10) && !schedule.TryBegin(1500, 8, "region:1", true, false, false, out _), "新区完成两次后未休眠");
});
Test("真实居中旁白经宽ROI缩小后仍足够亮点，可通过稳定门控", () =>
{
    Check(real.Count(p => p != 0) >= 12 && resumed.Count(p => p != 0) >= 12, "字幕被宽框缩小到空白门限以下");
    var gate = new SubtitleStability();
    Check(gate.Observe(real).Changed && !gate.Observe(real).Stable && gate.Observe(real).Stable, "真实宽框掩码不能稳定");
});
Test("两张同一句真图的箭头/采样微差不会重复重置共识", () =>
{
    var gate = new SubtitleStability(); gate.Observe(real); gate.Observe(real); gate.Observe(real);
    var observed = gate.Observe(resumed);
    Check(!observed.Changed && observed.Stable, "同句微差使跟随反复失稳");
});

int difference = real.Zip(resumed).Count(p => p.First != p.Second);
int union = real.Zip(resumed).Count(p => p.First != 0 || p.Second != 0);
var report = new { passed = failures == 0, count = tests.Count, failures, tests,
    masks = new { firstInk = real.Count(p => p != 0), resumedInk = resumed.Count(p => p != 0), difference, union },
    scope = "直接测试生产核心FollowSafetyController、SubtitleStability、OcrInferenceSchedule；按AppSession现有调用协议输入切区事件。没有执行Android AppSession，原始旧regionGeneration回调守卫只读审查。Pillow掩码只是主机近似。" };
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
Console.WriteLine(json);
if (args.Length > 0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!); File.WriteAllText(args[0], json); }
return failures == 0 ? 0 : 1;

static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static FollowObservation Observation(FollowSafetyController follow, long sequence, string key) =>
    new(follow.Epoch, sequence, key, true, [new() { Text = "第二句完整台词", Score = .99 }], "section");
static (PlaybackEngine, FollowSafetyController) Fixture()
{
    var pack = new Pack
    {
        Id = "跨区域安全回归", SchemaVersion = 3,
        Chapters = [new() { Id = "chapter", Sections = [new() { Id = "section", StartId = "first" }] }],
        Nodes = [new() { Id = "first", SectionId = "section", Text = "第一句完整台词", NextId = "second" },
            new() { Id = "second", SectionId = "section", Text = "第二句完整台词", NextId = "third" },
            new() { Id = "third", SectionId = "section", Text = "第三句完整台词" }]
    };
    pack.Validate(); var engine = new PlaybackEngine(pack); engine.Commit("first");
    var follow = new FollowSafetyController(); follow.SetMode(FollowMode.Automatic);
    Check(follow.ConfirmPosition(engine, "region:0/current"), "夹具当前位置无法确认");
    return (engine, follow);
}
