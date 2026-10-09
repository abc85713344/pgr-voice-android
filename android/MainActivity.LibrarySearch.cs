using Android.App;
using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Ui;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    readonly LibrarySearchIndex librarySearchIndex = new();
    CancellationTokenSource? librarySearchCancellation;

    void ShowLibrarySearch()
    {
        librarySearchCancellation?.Cancel();
        var holder = new LinearLayout(this) { Orientation = Orientation.Vertical };
        holder.SetPadding(Dp(16), Dp(6), Dp(16), Dp(6));
        var input = new EditText(this) { Hint = "角色名或台词；空格分隔多个关键词" };
        input.SetSingleLine(true); PgrTheme.StyleInput(input); holder.AddView(input);
        var note = Text("搜索已导入的章节。查看结果不改变播放位置。", 12); holder.AddView(note);
        var actions = Row(holder);
        var scroll = new ScrollView(this);
        var resultsView = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scroll.AddView(resultsView); holder.AddView(scroll, new LinearLayout.LayoutParams(-1, Dp(300)));
        var pager = Row(holder);
        LibrarySearchReport? report = null; int resultPage = 0; bool open = true;
        var builder = new AlertDialog.Builder(this); builder.SetTitle("跨章节搜索"); builder.SetView(holder);
        builder.SetNegativeButton("关闭", (_, _) => { });
        var dialog = builder.Create() ?? throw new InvalidOperationException("无法打开搜索窗口。");
        dialog.DismissEvent += (_, _) => { open = false; librarySearchCancellation?.Cancel(); };
        void RenderResults()
        {
            resultsView.RemoveAllViews(); pager.RemoveAllViews();
            if (report == null) return;
            foreach (var result in report.Results.Skip(resultPage * 25).Take(25))
                Button(resultsView, result.ToString(), () => ShowLibrarySearchPreview(result, dialog));
            if (resultPage > 0) Button(pager, "上一页", () => { resultPage--; RenderResults(); });
            if ((resultPage + 1) * 25 < report.Results.Count) Button(pager, "下一页", () => { resultPage++; RenderResults(); });
            scroll.ScrollTo(0, 0);
        }
        async void Search(bool refresh)
        {
            librarySearchCancellation?.Cancel();
            var cancellation = librarySearchCancellation = new CancellationTokenSource();
            string query = input.Text?.Trim() ?? "";
            report = null; resultPage = 0; RenderResults();
            if (query.Length == 0) { note.Text = "请输入角色名或一段台词。"; return; }
            note.Text = "正在搜索章节文本…";
            try
            {
                var files = session.Packages.List().Select(p => p.PackFile).ToArray();
                var progress = new Progress<LibrarySearchProgress>(p =>
                { if (open && alive && ReferenceEquals(cancellation, librarySearchCancellation)) note.Text = $"正在搜索 · {p.Completed}/{p.Total} 章"; });
                var found = await librarySearchIndex.SearchAsync(files, query, cancellation.Token, progress, refresh);
                if (!open || !alive || cancellation.IsCancellationRequested || !ReferenceEquals(cancellation, librarySearchCancellation)) return;
                report = found; resultPage = 0;
                note.Text = $"搜索 {found.SearchedPacks} 章 · 找到 {found.TotalMatches} 句" +
                    (found.TotalMatches > found.Results.Count ? $" · 显示前 {found.Results.Count} 句，请增加关键词" : "") +
                    (found.Problems.Count > 0 ? $"\n{found.Problems.Count} 个章节暂不可读，可更新后重新读取。" : "");
                RenderResults();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            { if (open && alive && !cancellation.IsCancellationRequested && ReferenceEquals(cancellation, librarySearchCancellation)) note.Text = "搜索未完成：" + ex.Message; }
        }
        Button(actions, "搜索", () => Search(false));
        Button(actions, "重新读取", () => Search(true));
        dialog.Show(); StyleDialog(dialog);
    }

    void ShowLibrarySearchPreview(LibrarySearchResult hit, AlertDialog searchDialog)
    {
        var searchRequest = librarySearchCancellation;
        bool RequestStillActive() => alive && searchDialog.IsShowing && searchRequest != null &&
            !searchRequest.IsCancellationRequested && ReferenceEquals(searchRequest, librarySearchCancellation);
        var preview = new AlertDialog.Builder(this);
        preview.SetTitle("台词预览");
        preview.SetMessage(hit + "\n\n台词编号：" + hit.NodeId + "\n\n打开后只显示台词所在位置；确认游戏当前句仍使用原有入口。");
        preview.SetNegativeButton("返回结果", (_, _) => { });
        preview.SetPositiveButton("在剧情页打开", async (_, _) =>
        {
            try
            {
                if (!RequestStillActive()) return;
                if (session.Engine?.Mode == RunMode.Original) throw new InvalidOperationException("当前处于游戏原声时段，请先按原流程确认续接位置。");
                if (session.PackagesBusy) throw new InvalidOperationException("章节文件正在处理中，请稍后重新搜索。");
                var engineBefore = session.Engine;
                var nodeBefore = engineBefore?.CurrentId;
                var modeBefore = engineBefore?.Mode;
                var sectionBefore = session.SectionId;
                var listeningPackBefore = session.Listening.Pack;
                var listeningNodeBefore = session.Listening.Current?.Id;
                var target = await Task.Run(() => LibrarySearchIndex.Resolve(hit));
                if (!RequestStillActive()) return;
                if (!ReferenceEquals(engineBefore, session.Engine) || session.Engine?.CurrentId != nodeBefore || session.Engine?.Mode != modeBefore ||
                    session.SectionId != sectionBefore || session.Listening.Pack != listeningPackBefore || session.Listening.Current?.Id != listeningNodeBefore ||
                    session.Engine?.Mode == RunMode.Original || session.PackagesBusy)
                    throw new InvalidOperationException("播放章节或文件状态已变化，请重新打开搜索结果。");
                var installed = session.Packages.Find(hit.PackId);
                if (installed == null || Path.GetFullPath(installed.PackFile) != Path.GetFullPath(hit.PackFile))
                    throw new InvalidOperationException("章节已更新或移除，请重新读取后搜索。");
                // 同图也可能已更新正文/音频；明确打开时重读当前清单，进度仍由原加载流程恢复。
                session.LoadPack(hit.PackId);
                var actual = session.Engine!.Pack;
                if (PlaybackEngine.NavigationFingerprint(actual) != PlaybackEngine.NavigationFingerprint(target.Pack) ||
                    !actual.ById.TryGetValue(hit.NodeId, out var node) || node.Archived || node.Kind != "line" || node.SectionId != hit.SectionId || node.PathId != hit.PathId || node.Text != hit.Text || node.Speaker != hit.Speaker)
                    throw new InvalidOperationException("台词版本已变化，请重新搜索。");
                session.SectionId = hit.SectionId;
                var lines = actual.Nodes.Where(n => !n.Archived && n.Kind == "line" && n.SectionId == hit.SectionId).ToList();
                linePage = Math.Max(0, lines.FindIndex(n => n.Id == hit.NodeId)) / 45;
                page = 1; searchDialog.Dismiss(); Render();
                ConfirmNode(node, () => ReferenceEquals(actual, session.Engine?.Pack));
            }
            catch (Exception ex) { if (RequestStillActive()) Info("搜索结果未打开", ex.Message); }
        });
        StyleDialog(preview.Show());
    }
}
