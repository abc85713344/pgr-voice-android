#if DEBUG
using Android.App;
using Android.OS;
using Android.Widget;
using PgrVoice.Listening;
using System.Text.Json;

namespace PgrVoice.AndroidApp.Diagnostics;

/// <summary>仅 Debug 导出的真实听书回归入口；Release 预处理后不存在此类型或 Activity 注册。</summary>
[Activity(Name = "cn.pgrvoice.player.ListeningDiagnosticActivity", Exported = true, NoHistory = true)]
public sealed class ListeningDiagnosticActivity : Activity
{
    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(new TextView(this) { Text = "独立听书回归\n合成测试音频，非真实游戏。\n测试结束后自动关闭。", TextSize = 20 });
        string directory = FilesDir!.AbsolutePath;
        string output = Path.Combine(directory, "listening-report.json");
        long started = SystemClock.ElapsedRealtime();
        var results = new List<object>();
        var observations = new List<object>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        AppSession? app = null;
        Action? observer = null;
        try
        {
            app = AppSession.Get(this);
            app.SetUiVisible(true);
            var player = app.Listening;
            string scriptName = SimpleFile(Intent?.GetStringExtra("script") ?? "listening-test.json");
            var commands = JsonSerializer.Deserialize<List<TestCommand>>(
                File.ReadAllText(Path.Combine(directory, scriptName)), Json.Options) ?? throw new InvalidDataException("听书脚本为空。");
            if (commands.Count is < 1 or > 500) throw new InvalidDataException("听书脚本需要 1–500 条命令。");
            observer = () =>
            {
                if (player.Current?.Id is { } id) visited.Add(id);
                if (observations.Count < 5000) observations.Add(Snapshot(app, started));
            };
            player.Changed += observer;
            WriteReport(output, new { complete = false, passed = false, results, observations });
            for (int index = 0; index < commands.Count; index++)
            {
                var command = commands[index];
                Exception? failure = null;
                try
                {
                    switch (command.Action)
                    {
                        case "import":
                            using (var input = File.OpenRead(Path.Combine(directory, SimpleFile(command.Value))))
                                await app.ImportAsync(input, CancellationToken.None);
                            break;
                        case "open": player.Open(Required(command.PackId, "packId"), Required(command.ChapterId, "chapterId")); break;
                        case "policy": player.SetPolicy(ParsePolicy(command.Value)); break;
                        case "play": player.Play(); break;
                        case "pause": player.Pause(); break;
                        case "next": player.Next(); break;
                        case "previous": player.Previous(); break;
                        case "section": player.JumpToSection(command.Value); break;
                        case "node": player.JumpToNode(command.Value); break;
                        case "choose": player.Choose(command.Value); break;
                        case "bookmark": player.AddBookmark(command.Value); break;
                        case "restore":
                            var bookmark = player.Bookmarks.LastOrDefault(b => b.Id == command.Value || b.Label == command.Value)
                                ?? throw new InvalidOperationException("听书书签不存在：" + command.Value);
                            player.RestoreBookmark(bookmark.Id); break;
                        case "speed": player.SetSpeed((float)command.Number); break;
                        case "sleep": player.SetSleepMinutes(checked((int)command.Number)); break;
                        case "seek": player.SeekMilliseconds(checked((long)command.Number)); break;
                        case "stop": player.Stop(); break;
                        case "wait": break;
                        default: throw new InvalidDataException("未知听书测试动作：" + command.Action);
                    }
                    if (command.WaitMs is < 0 or > 120_000) throw new InvalidDataException("waitMs 必须在 0–120000 范围内。");
                    if (command.WaitMs > 0) await Task.Delay(command.WaitMs);
                }
                catch (Exception ex) { failure = ex; }

                observer();
                var errors = new List<string>();
                if (command.ExpectException == null && failure != null) errors.Add("意外异常：" + failure.Message);
                if (command.ExpectException != null && (failure == null ||
                    !failure.ToString().Contains(command.ExpectException, StringComparison.OrdinalIgnoreCase)))
                    errors.Add("未得到期待异常：" + command.ExpectException);
                if (command.ExpectNode != null && player.Current?.Id != command.ExpectNode) errors.Add("当前节点不符：" + command.ExpectNode);
                if (command.ExpectNoNode == true && player.Current != null) errors.Add("期待当前节点为空。");
                if (command.ExpectPlaying is { } playing && player.IsPlaying != playing) errors.Add("播放状态不符。");
                if (command.ExpectChoiceCount is { } choices && player.Choices.Count != choices) errors.Add("分支选项数量不符。");
                if (command.ExpectChapter != null && player.Chapter?.Id != command.ExpectChapter) errors.Add("当前大章不符。");
                if (command.ExpectBookmarkCount is { } count && player.Bookmarks.Count != count) errors.Add("书签数量不符。");
                if (command.ExpectPositionMinMs is { } minimum && player.PositionMilliseconds < minimum) errors.Add("句内位置小于期待值。");
                if (command.ExpectPositionMaxMs is { } maximum && player.PositionMilliseconds > maximum) errors.Add("句内位置大于期待值。");
                if (command.ExpectStatusContains != null && !player.Status.Contains(command.ExpectStatusContains, StringComparison.Ordinal)) errors.Add("状态提示不符。");
                if (command.ExpectVisited != null)
                    foreach (string id in command.ExpectVisited.Where(id => !visited.Contains(id))) errors.Add("没有观察到节点：" + id);
                bool passed = errors.Count == 0;
                results.Add(new { index, command.Action, command.Value, passed, errors, exception = failure?.ToString(), state = Snapshot(app, started) });
                WriteReport(output, new { complete = false, passed = false, results, observations });
                if (!passed) throw new InvalidOperationException("听书回归断言失败，第 " + (index + 1) + " 步：" + string.Join("；", errors));
            }
            WriteReport(output, new { complete = true, passed = true, elapsedMs = SystemClock.ElapsedRealtime() - started, results, observations });
        }
        catch (Exception ex)
        {
            try { app?.Listening.Stop(); } catch { }
            WriteReport(output, new { complete = true, passed = false, elapsedMs = SystemClock.ElapsedRealtime() - started, results, observations, error = ex.ToString() });
        }
        finally
        {
            if (app != null && observer != null) app.Listening.Changed -= observer;
            Finish();
        }
    }

    static object Snapshot(AppSession app, long started)
    {
        var player = app.Listening;
        return new
        {
            elapsedMs = SystemClock.ElapsedRealtime() - started,
            packId = player.Pack?.Id, chapterId = player.Chapter?.Id,
            currentId = player.Current?.Id, currentKind = player.Current?.Kind,
            isPlaying = player.IsPlaying, positionMilliseconds = player.PositionMilliseconds,
            durationMilliseconds = player.DurationMilliseconds, status = player.Status,
            positionText = player.PositionText, resumeText = player.ResumeText,
            policy = player.Policy.ToString(), speed = player.Speed, sleepText = player.SleepText,
            choices = player.Choices, bookmarks = player.Bookmarks,
            gameNodeId = app.Engine?.CurrentId, gameMode = app.Engine?.Mode.ToString(),
            gameAutomaticRunning = app.AutoPlaybackRunning
        };
    }

    static string SimpleFile(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Any(c =>
            !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.')) || name is "." or "..")
            throw new InvalidDataException("测试文件名必须是单个 ASCII 文件名。");
        return name;
    }
    static string Required(string? value, string field) => !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("缺少 " + field);
    static ListeningBranchPolicy ParsePolicy(string value) => value.ToLowerInvariant() switch
    {
        "first" => ListeningBranchPolicy.First, "all" => ListeningBranchPolicy.All, "manual" => ListeningBranchPolicy.Manual,
        _ => throw new InvalidDataException("policy 需要 first/all/manual。")
    };
    static void WriteReport(string path, object value)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json.Options));
        File.Move(temporary, path, true);
    }
    public sealed class TestCommand
    {
        public string Action { get; set; } = "";
        public string Value { get; set; } = "";
        public string? PackId { get; set; }
        public string? ChapterId { get; set; }
        public double Number { get; set; }
        public int WaitMs { get; set; } = 150;
        public string? ExpectNode { get; set; }
        public bool? ExpectNoNode { get; set; }
        public bool? ExpectPlaying { get; set; }
        public int? ExpectChoiceCount { get; set; }
        public string? ExpectChapter { get; set; }
        public int? ExpectBookmarkCount { get; set; }
        public long? ExpectPositionMinMs { get; set; }
        public long? ExpectPositionMaxMs { get; set; }
        public string? ExpectStatusContains { get; set; }
        public string? ExpectException { get; set; }
        public string[]? ExpectVisited { get; set; }
    }
}
#endif
