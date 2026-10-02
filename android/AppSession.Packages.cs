using PgrVoice.Packages;

namespace PgrVoice.AndroidApp;

public sealed record ImportedZipSource(string Uri, string Name, long? Size, long? Modified);

public sealed partial class AppSession
{
    public bool IsDeletingPackage { get; private set; }
    public bool PackagesBusy => IsImporting || IsDeletingPackage;

    public void ForgetImportedArchive(string uri)
    {
        foreach (string id in Settings.ImportedArchives.Where(p => p.Value.Uri == uri).Select(p => p.Key).ToArray())
            Settings.ImportedArchives.Remove(id);
        SaveSettings(); ContentChanged?.Invoke();
    }

    public async Task RemovePackageAsync(string packId, string revision)
    {
        if (PackagesBusy) throw new InvalidOperationException("章节文件正在处理中，请等待完成。");
        if (Packages.Find(packId)?.Revision != revision)
            throw new InvalidOperationException("章节已更新或删除，请重新打开管理页。");
        IsDeletingPackage = true;
        try
        {
            if (Engine?.Pack.Id == packId)
            {
                Invalidate(); audio.Stop(); Engine.PauseForBrowse(); SaveProgress(); saveQueue.Flush();
                Engine = null; SectionId = ""; OcrText = "尚未识别";
                overlay.ClearAutoPlaybackPaused();
            }
            Listening.UnloadPackage(packId);
            if (Settings.LastPackId == packId) { Settings.LastPackId = null; SaveSettings(); }
            Status = "正在删除章节配音包…"; ContentChanged?.Invoke(); Notify();
            // 移动和逐文件回收放在后台，避免大章节阻塞触屏。
            var result = await Task.Run(() => Packages.RemoveAsync(packId, revision));
            Status = result.CleanupPending ? "章节已移除，部分占用中的文件将在下次启动时清理。进度和书签已保留。" :
                "章节配音包已删除，进度和书签已保留；重新导入同一章节可继续使用。";
        }
        finally { IsDeletingPackage = false; ContentChanged?.Invoke(); Notify(); }
    }
}
