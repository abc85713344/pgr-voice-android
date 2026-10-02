using Android.App;
using Android.Content;
using Android.Widget;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.Packages;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    AlertDialog? importDialog;
    sealed record ZipImportOutcome(string Name, InstalledPackage? Package, string? Error);

    async Task ImportSelectedZipsAsync(Intent data)
    {
        if (importCancellation != null || session.PackagesBusy || deletingArchive)
            throw new InvalidOperationException("章节文件正在处理中，请等待完成。");
        // 多选通常只有 ClipData，没有 Data；有些提供方同时返回两者。
        var selected = new List<string>();
        void Add(global::Android.Net.Uri? uri)
        { if (uri != null && !selected.Contains(uri.ToString()!, StringComparer.Ordinal)) selected.Add(uri.ToString()!); }
        if (data.ClipData is { } clips)
            for (int i = 0; i < clips.ItemCount; i++) Add(clips.GetItemAt(i)?.Uri);
        Add(data.Data);
        if (selected.Count == 0) return;

        using var cancellation = new CancellationTokenSource();
        importCancellation = cancellation;
        var outcomes = new List<ZipImportOutcome>();
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.SetPadding(Dp(20), Dp(10), Dp(20), Dp(12));
        var file = Text("准备读取所选 ZIP…", 15); box.AddView(file);
        var stage = Text("", 12); box.AddView(stage);
        var bar = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
            { Max = selected.Count, Progress = 0 };
        box.AddView(bar);
        box.AddView(Text("按顺序逐个处理。取消会停止当前及后续导入，已完成的章节会保留。", 12));
        importDialog = new AlertDialog.Builder(this).SetTitle($"导入 {selected.Count} 个 ZIP")!
            .SetView(box)!.SetCancelable(false)!.SetNegativeButton("取消导入", (_, _) => { })!.Create();
        void UpdateImportStage() { if (alive) stage.Text = session.Status; }
        session.Changed += UpdateImportStage;
        try
        {
            importDialog!.Show(); StyleDialog(importDialog);
            var cancel = importDialog.GetButton((int)DialogButtonType.Negative)!;
            cancel.Click += (_, _) =>
            {
                cancellation.Cancel(); cancel.Enabled = false; cancel.Text = "正在取消…";
            };
            Render();
            for (int i = 0; i < selected.Count; i++)
            {
                if (cancellation.IsCancellationRequested) break;
                string name = $"所选文件 {i + 1}";
                file.Text = $"第 {i + 1}/{selected.Count} 个 · 正在读取文件信息…";
                try
                {
                    using var uri = global::Android.Net.Uri.Parse(selected[i])!;
                    ImportedZipSource? source = null;
                    try { source = await Task.Run(() => ImportedZipAccess.Read(ApplicationContext!, uri)); }
                    catch (Exception ex) { session.Diagnostics.Log("原ZIP记录", ex.Message); }
                    name = source?.Name ?? name;
                    cancellation.Token.ThrowIfCancellationRequested();
                    file.Text = $"第 {i + 1}/{selected.Count} 个 · {name}";
                    InstalledPackage? installed;
                    // 一次只打开一个压缩包；输入流关闭后才展示文件管理和删除入口。
                    using (var stream = await Task.Run(() => ContentResolver!.OpenInputStream(uri)
                        ?? throw new IOException("无法读取所选 ZIP。")))
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        installed = await session.ImportAsync(stream, cancellation.Token, source);
                    }
                    if (installed == null) break;
                    if (source != null) ImportedZipAccess.PersistGrant(ApplicationContext!, uri, data.Flags);
                    outcomes.Add(new(name, installed, null));
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    session.Diagnostics.Log("ZIP导入失败", name + "：" + ex.Message);
                    outcomes.Add(new(name, null, ex is InvalidDataException ? "压缩包损坏或内容不符合要求：" + ex.Message : ex.Message));
                }
                bar.Progress = i + 1;
            }
        }
        finally
        {
            session.Changed -= UpdateImportStage;
            if (ReferenceEquals(importCancellation, cancellation)) importCancellation = null;
            importDialog?.Dismiss(); importDialog = null;
            if (alive) { page = 0; Render(); }
        }
        if (!alive) return;
        if (selected.Count == 1 && outcomes.Count == 1 && outcomes[0].Package is { } single)
            ManagePackage(single.PackId, true);
        else ShowImportSummary(outcomes, selected.Count);
    }

    void ShowImportSummary(IReadOnlyList<ZipImportOutcome> outcomes, int total)
    {
        int success = outcomes.Count(r => r.Package != null), failed = outcomes.Count - success;
        int remaining = total - outcomes.Count;
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.SetPadding(Dp(18), Dp(8), Dp(18), Dp(12));
        box.AddView(Text($"成功 {success} · 失败 {failed} · 未完成 {remaining}", 17));
        if (remaining > 0) box.AddView(Text("已取消剩余导入，已成功导入的章节继续保留。", 13));
        if (success > 0) box.AddView(Text("点成功项管理文件，可删除原 ZIP 或查看解压目录。", 12));
        AlertDialog? dialog = null;
        foreach (var result in outcomes)
        {
            if (result.Package is { } pack)
                Button(box, (pack.IsUpdate ? "已更新 · " : "已导入 · ") + result.Name,
                    () => { dialog?.Dismiss(); ManagePackage(pack.PackId); });
            else box.AddView(Text("失败 · " + result.Name + "\n" + result.Error, 13));
        }
        var scroll = new ScrollView(this); scroll.AddView(box);
        dialog = new AlertDialog.Builder(this).SetTitle(remaining > 0 ? "导入已取消" : "导入结果")!
            .SetView(scroll)!.SetPositiveButton("完成", (_, _) => { })!.Create();
        dialog!.Show(); StyleDialog(dialog);
    }
}
