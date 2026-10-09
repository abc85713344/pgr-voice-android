using Android.App;
using Android.Widget;
using PgrVoice.AndroidApp.Ui;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    void ShowStoryReference()
    {
        if (session.Engine is not { } engine)
        { Info("内置剧情文本", "请先打开章节，再查看当前小节的剧情文本。"); return; }
        // 固定打开时的章节与小节；查找、翻阅、关闭都不提交播放或定位命令。
        var pack = engine.Pack; string sectionId = session.SectionId;
        string sectionText = BundledStoryReference.GetSectionText(pack, sectionId);
        string sectionTitle = SectionDisplay.Title(pack.Chapters.SelectMany(c => c.Sections).ToList(), sectionId);
        var holder = new LinearLayout(this) { Orientation = Orientation.Vertical };
        holder.SetPadding(Dp(16), Dp(6), Dp(16), Dp(6));
        var caption = Text(PackTitle(pack.Title) + " · " + sectionTitle, 14); caption.SetMaxLines(2); holder.AddView(caption);
        var summary = Text(BundledStoryReference.GetSummary(pack), 12); summary.SetMaxLines(2); holder.AddView(summary);
        var input = new EditText(this) { Hint = "在本小节查找台词或角色" };
        input.SetSingleLine(true); PgrTheme.StyleInput(input); holder.AddView(input);
        var actions = Row(holder);
        var result = Text("只读资料。查看和查找不会改变配音位置。", 12); result.SetMaxLines(3); holder.AddView(result);
        var scroll = new ScrollView(this);
        var body = Text(sectionText, 14); body.SetTextIsSelectable(true); body.SetPadding(0, Dp(6), 0, Dp(12));
        int screenHeight = (int)(Resources!.DisplayMetrics!.HeightPixels / Resources.DisplayMetrics.Density);
        int textHeight = Math.Clamp(screenHeight - 330, 80, 440);
        scroll.AddView(body); holder.AddView(scroll, new LinearLayout.LayoutParams(-1, Dp(textHeight)));
        var builder = new AlertDialog.Builder(this); builder.SetTitle("内置剧情文本 · 当前小节"); builder.SetView(holder);
        builder.SetNegativeButton("关闭", (_, _) => { });
        var dialog = builder.Create() ?? throw new InvalidOperationException("无法打开剧情文本。");
        bool open = true; string searched = ""; var matches = new List<int>(); int currentMatch = -1;
        dialog.DismissEvent += (_, _) => open = false;

        void Find(bool next)
        {
            if (!open || !alive) return;
            string query = input.Text?.Trim() ?? "";
            if (query.Length == 0) { result.Text = "请输入本小节中的台词或角色名。"; return; }
            if (!next || query != searched)
            {
                searched = query; matches.Clear(); currentMatch = -1;
                int offset = 0;
                while (offset <= sectionText.Length - query.Length)
                {
                    int found = sectionText.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                    if (found < 0) break;
                    matches.Add(found); offset = found + query.Length;
                }
            }
            if (matches.Count == 0) { result.Text = "本小节未找到“" + query + "”。"; return; }
            currentMatch = (currentMatch + 1) % matches.Count;
            int position = matches[currentMatch];
            int before = Math.Max(0, position - 45), after = Math.Min(sectionText.Length, position + query.Length + 65);
            string excerpt = (before > 0 ? "…" : "") + sectionText[before..position].Replace('\n', ' ') +
                "【" + sectionText.Substring(position, query.Length) + "】" + sectionText[(position + query.Length)..after].Replace('\n', ' ') +
                (after < sectionText.Length ? "…" : "");
            result.Text = $"第 {currentMatch + 1}/{matches.Count} 处\n" + excerpt;
            body.Post(() =>
            {
                if (!open || !alive || body.Layout is not { } layout) return;
                scroll.SmoothScrollTo(0, Math.Max(0, layout.GetLineTop(layout.GetLineForOffset(position)) - Dp(8)));
            });
        }
        Button(actions, "查找", () => Find(false));
        Button(actions, "下一处", () => Find(true));
        dialog.Show(); StyleDialog(dialog);
    }
}
