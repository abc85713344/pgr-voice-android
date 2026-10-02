using Android.App;
using Android.Content;
using Android.Widget;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.AndroidApp.Ui;
using PgrVoice.Packages;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    bool deletingArchive;
    static string FileSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.##} GB" :
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.##} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";

    void ManagePackage(string packId, bool justImported = false)
    {
        if (session.PackagesBusy) { Info("章节正在处理", "请等当前导入或删除完成后再管理文件。"); return; }
        var pack = session.Packages.Find(packId) ?? throw new IOException("章节已被删除。");
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.SetPadding(Dp(18), Dp(8), Dp(18), Dp(12));
        box.AddView(Text(justImported ? "导入成功，已解压的章节可以独立播放。" : pack.Title, 15));
        var location = Text("解压文件夹 · 点击进入\n" + System.IO.Path.GetDirectoryName(pack.PackFile), 12);
        location.SetTextColor(PgrTheme.Cyan); location.ContentDescription = "打开章节解压文件夹";
        location.Clickable = true; location.Focusable = true; box.AddView(location);
        AlertDialog? dialog = null;
        location.Click += (_, _) => { dialog?.Dismiss(); BrowsePackage(packId, "", pack.Revision); };
        box.AddView(Text("应用内部存储，点上方路径可在这里浏览文件。", 11));
        if (session.Settings.ImportedArchives.TryGetValue(packId, out var source))
        {
            box.AddView(Text("原 ZIP：" + source.Name + (source.Size is >= 0 ? " · " + FileSize(source.Size.Value) : ""), 13));
            Button(box, "删除原 ZIP（保留已导入章节）", () =>
            {
                dialog?.Dismiss(); ConfirmDeleteArchive(packId, source);
            });
        }
        else box.AddView(Text("未记录原 ZIP，或已从应用中删除。旧版导入的压缩包请在系统文件管理中处理。", 12));
        Button(box, "删除章节配音包", () =>
        {
            dialog?.Dismiss();
            Confirm("删除章节配音包？", pack.Title + "\n\n将删除本章解压文件及旧版本音频，停止本章播放。\n保留游戏配音进度、听书记录和书签；原 ZIP 不会删除。\n再次播放需要重新导入。", () => DeletePackage(pack));
        });
        var scroll = new ScrollView(this); scroll.AddView(box);
        var builder = new AlertDialog.Builder(this).SetTitle(justImported ? "章节导入完成" : "管理章节文件")!
            .SetView(scroll)!.SetNegativeButton("关闭", (_, _) => { });
        dialog = builder!.Create(); dialog!.Show(); StyleDialog(dialog);
    }

    void ConfirmDeleteArchive(string packId, ImportedZipSource source)
    {
        Confirm("删除原 ZIP？", source.Name + "\n\n只删除导入时选中的压缩包，已解压的章节、进度和书签会保留。\n这是原文件，删除后如需备份须重新下载。", async () =>
        {
            if (deletingArchive || session.PackagesBusy) { Info("请稍候", "文件正在处理中，请等待完成。"); return; }
            deletingArchive = true;
            try
            {
                if (!session.Settings.ImportedArchives.TryGetValue(packId, out var now) || now != source || session.Packages.Find(packId) == null)
                { Info("文件记录已变化", "请重新打开章节管理后再操作。"); return; }
                await Task.Run(() => ImportedZipAccess.Delete(ApplicationContext!, source));
                session.ForgetImportedArchive(source.Uri);
                if (alive) { Render(); Info("原 ZIP 已删除", "已解压的章节、进度和书签都已保留，可以继续播放。"); }
            }
            catch (Exception ex)
            {
                if (alive) Info("未能删除原 ZIP", ex is Java.Lang.SecurityException ?
                    "原文件的删除授权不可用，请在系统文件管理中删除。已导入的章节不受影响。" : ex.Message);
            }
            finally { deletingArchive = false; }
        });
    }

    async void DeletePackage(InstalledPackage pack)
    {
        if (deletingArchive) { Info("请稍候", "正在删除原 ZIP，请等待完成。"); return; }
        try { await session.RemovePackageAsync(pack.PackId, pack.Revision); if (alive) { page = 0; Render(); Info("章节已删除", session.Status); } }
        catch (Exception ex) { if (alive) Info("未能删除章节", ex.Message); }
    }

    async void BrowsePackage(string packId, string relative, string? revision, int pageIndex = 0)
    {
        try
        {
            var folder = await Task.Run(() => session.Packages.Browse(packId, relative, revision));
            if (!alive) return;
            var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
            box.SetPadding(Dp(18), Dp(8), Dp(18), Dp(8));
            var path = Text(folder.FullPath, 12); path.SetTextIsSelectable(true); box.AddView(path);
            Button(box, "复制路径", () =>
            {
                var clipboard = (ClipboardManager)GetSystemService(ClipboardService)!;
                clipboard.PrimaryClip = ClipData.NewPlainText("章节文件位置", folder.FullPath);
                Toast.MakeText(this, "路径已复制", ToastLength.Short)?.Show();
            });
            AlertDialog? dialog = null;
            if (relative.Length > 0) Button(box, "↑ 上一级", () =>
            {
                dialog?.Dismiss(); int slash = relative.LastIndexOf('/');
                BrowsePackage(packId, slash < 0 ? "" : relative[..slash], folder.Revision);
            });
            const int pageSize = 40;
            int pages = Math.Max(1, (folder.Entries.Count + pageSize - 1) / pageSize);
            pageIndex = Math.Clamp(pageIndex, 0, pages - 1);
            box.AddView(Text($"{folder.Entries.Count} 项 · 第 {pageIndex + 1}/{pages} 页", 12));
            foreach (var entry in folder.Entries.Skip(pageIndex * pageSize).Take(pageSize))
            {
                if (entry.IsDirectory) Button(box, "文件夹  " + entry.Name + "  ›", () =>
                { dialog?.Dismiss(); BrowsePackage(packId, entry.RelativePath, folder.Revision); });
                else box.AddView(Text(entry.Name + "\n" + FileSize(entry.Bytes), 13));
            }
            if (pageIndex > 0) Button(box, "上一页", () => { dialog?.Dismiss(); BrowsePackage(packId, relative, folder.Revision, pageIndex - 1); });
            if (pageIndex + 1 < pages) Button(box, "下一页", () => { dialog?.Dismiss(); BrowsePackage(packId, relative, folder.Revision, pageIndex + 1); });
            var scroll = new ScrollView(this); scroll.AddView(box);
            var builder = new AlertDialog.Builder(this).SetTitle("章节解压文件")!.SetView(scroll)!
                .SetNegativeButton("关闭", (_, _) => { })!.SetNeutralButton("章节管理", (_, _) => Safe(() => ManagePackage(packId)));
            dialog = builder!.Create(); dialog!.Show(); StyleDialog(dialog);
        }
        catch (Exception ex) { if (alive) Info("无法打开文件夹", ex.Message); }
    }
}
