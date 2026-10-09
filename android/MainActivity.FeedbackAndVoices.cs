using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Ui;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    string FeedbackVersion => PackageManager?.GetPackageInfo(PackageName!, PackageInfoFlags.MetaData)?.VersionName ?? "未知版本";

    IEnumerable<Node> RecordedFeedbackContext(PlaybackEngine engine, int throughIndex) =>
        engine.History.Take(Math.Clamp(throughIndex + 1, 0, engine.History.Count)).TakeLast(4)
            .Select(v => engine.Pack.ById.GetValueOrDefault(v.NodeId)).OfType<Node>();

    void FeedbackCurrentGameLine()
    {
        var engine = session.Engine;
        if (engine?.Current is not { Kind: "line" } node)
        { Info("反馈这句", "请先选择要反馈的台词，或在历史记录中选择已播放的句子。"); return; }
        BeginLineFeedback(engine.Pack, node, "游戏配音", RecordedFeedbackContext(engine, engine.HistoryPosition));
    }

    void FeedbackCurrentListeningLine()
    {
        var player = session.Listening;
        if (player.Pack == null || player.Current is not { Kind: "line" } node)
        { Info("反馈这句", "请先选择要反馈的听书台词。"); return; }
        BeginLineFeedback(player.Pack, node, "听书", listening: true);
    }

    void BeginLineFeedback(Pack pack, Node node, string mode, IEnumerable<Node>? recent = null, bool listening = false)
    {
        // 先冻结选中句，再暂停；填写期间任何位置变化都不会替换反馈对象。
        var snapshot = LineFeedback.Capture(pack, node, FeedbackVersion, "Android", mode, recent);
        if (listening) session.Listening.Pause();
        else session.Command(e => e.PauseForBrowse());
        ShowLineFeedbackCategories(snapshot);
    }

    void ShowLineFeedbackCategories(LineFeedbackSnapshot snapshot) =>
        Choose("反馈这句 · 选择问题", LineFeedback.Categories.ToArray(),
            i => EditLineFeedback(snapshot, LineFeedback.Categories[i]));

    void EditLineFeedback(LineFeedbackSnapshot snapshot, string category, string comment = "")
    {
        var holder = new LinearLayout(this) { Orientation = Orientation.Vertical };
        holder.SetPadding(Dp(20), Dp(8), Dp(20), Dp(4));
        var original = Text(SpeakerVolume.DisplayName(snapshot.Speaker) + "：" + snapshot.Text, 14);
        original.SetMaxLines(4); original.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        holder.AddView(original);
        var edit = new EditText(this) { Text = comment, Hint = "补充说明（可留空）", Gravity = GravityFlags.Top };
        edit.SetMinLines(2); edit.SetMaxLines(5); PgrTheme.StyleInput(edit); holder.AddView(edit);
        var hint = Text("预览后可复制或自行选择应用分享。不会自动发送，也不采集截图或录屏。", 12);
        hint.SetTextColor(PgrTheme.Secondary); holder.AddView(hint);
        var dialog = new AlertDialog.Builder(this);
        dialog.SetTitle(category); dialog.SetView(holder);
        dialog.SetNegativeButton("取消", (_, _) => { });
        dialog.SetPositiveButton("预览反馈", (_, _) => Safe(() => PreviewLineFeedback(snapshot, category, edit.Text ?? "")));
        StyleDialog(dialog.Show());
    }

    void PreviewLineFeedback(LineFeedbackSnapshot snapshot, string category, string comment)
    {
        string report = LineFeedback.Format(snapshot, category, comment);
        var body = Text(report, 13); body.SetTextIsSelectable(true); body.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        var scroll = new ScrollView(this); scroll.AddView(body);
        var dialog = new AlertDialog.Builder(this);
        dialog.SetTitle("反馈预览 · " + category); dialog.SetView(scroll);
        dialog.SetNegativeButton("返回修改", (_, _) => Safe(() => EditLineFeedback(snapshot, category, comment)));
        dialog.SetNeutralButton("复制", (_, _) => Safe(() =>
        {
            var clipboard = (global::Android.Content.ClipboardManager)GetSystemService(ClipboardService)!;
            clipboard.PrimaryClip = ClipData.NewPlainText("台词反馈", report);
            Toast.MakeText(this, "反馈已复制，由你决定是否发送", ToastLength.Short)?.Show();
        }));
        dialog.SetPositiveButton("系统分享", (_, _) => Safe(() =>
        {
            var intent = new Intent(Intent.ActionSend);
            intent.SetType("text/plain"); intent.PutExtra(Intent.ExtraText, report);
            StartActivity(Intent.CreateChooser(intent, "选择分享应用"));
        }));
        StyleDialog(dialog.Show());
    }

    void ShowSpeakerVolumes()
    {
        var packs = new[] { session.Engine?.Pack, session.Listening.Pack }.OfType<Pack>().Distinct();
        var names = packs.SelectMany(p => p.Nodes).Where(n => !n.Archived && n.Kind == "line")
            .Select(n => SpeakerVolume.Key(n.Speaker)).Concat(session.Settings.SpeakerVolumes.Keys)
            .Distinct(StringComparer.Ordinal).OrderBy(n => SpeakerVolume.DisplayName(n), StringComparer.CurrentCulture).ToArray();
        if (names.Length == 0) { Info("角色音量", "请先打开一章剧情或听书，再调整其中的角色。"); return; }
        var holder = new LinearLayout(this) { Orientation = Orientation.Vertical };
        holder.SetPadding(Dp(16), Dp(8), Dp(16), Dp(4));
        var help = Text("按当前章节里的名字分别保存。100% 保持总音量，0% 静音；不会合并别名或更换声音。", 12);
        help.SetTextColor(PgrTheme.Secondary); holder.AddView(help);
        var search = new EditText(this) { Hint = "搜索角色" }; search.SetSingleLine(true); PgrTheme.StyleInput(search); holder.AddView(search);
        var rows = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var scroll = new ScrollView(this); scroll.AddView(rows); holder.AddView(scroll, new LinearLayout.LayoutParams(-1, Dp(280)));
        void Refresh()
        {
            rows.RemoveAllViews();
            foreach (string name in names.Where(n => SpeakerVolume.DisplayName(n).Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)))
            {
                string speaker = name;
                Button(rows, SpeakerVolume.DisplayName(name) + " · " + SpeakerVolume.GetPercent(session.Settings.SpeakerVolumes, name) + "%",
                    () => EditSpeakerVolume(speaker, Refresh));
            }
            if (rows.ChildCount == 0) rows.AddView(Text("没有匹配的角色", 13));
        }
        search.TextChanged += (_, _) => Refresh(); Refresh();
        var dialog = new AlertDialog.Builder(this); dialog.SetTitle("角色音量"); dialog.SetView(holder);
        dialog.SetPositiveButton("完成", (_, _) => { }); StyleDialog(dialog.Show());
    }

    void EditSpeakerVolume(string speaker, Action refresh)
    {
        var holder = new LinearLayout(this) { Orientation = Orientation.Vertical };
        holder.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        int percent = SpeakerVolume.GetPercent(session.Settings.SpeakerVolumes, speaker);
        var value = Text($"{percent}%", 20); holder.AddView(value);
        var slider = new SeekBar(this) { Max = 100, Progress = percent }; slider.SetMinimumHeight(Dp(48));
        slider.ContentDescription = SpeakerVolume.DisplayName(speaker) + "的音量百分比";
        slider.ProgressChanged += (_, e) => value.Text = $"{e.Progress}%"; holder.AddView(slider);
        holder.AddView(Text("保存后同时用于游戏配音、听书和试听；实际音量还会乘以总音量。", 12));
        var dialog = new AlertDialog.Builder(this); dialog.SetTitle(SpeakerVolume.DisplayName(speaker)); dialog.SetView(holder);
        dialog.SetNegativeButton("取消", (_, _) => { });
        dialog.SetNeutralButton("恢复 100%", (_, _) => Safe(() => { session.SetSpeakerVolume(speaker, 100); refresh(); if(session.HasSettingsSaveWarning)Info("设置尚未保存",session.Status); }));
        dialog.SetPositiveButton("保存", (_, _) => Safe(() => { session.SetSpeakerVolume(speaker, slider.Progress); refresh(); if(session.HasSettingsSaveWarning)Info("设置尚未保存",session.Status); }));
        StyleDialog(dialog.Show());
    }
}
