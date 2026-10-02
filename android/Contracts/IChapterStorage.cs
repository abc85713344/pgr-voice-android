using PgrVoice.Packages;

namespace PgrVoice.AndroidApp.Contracts;

/// <summary>章节仓库边界。输入流可以来自 SAF，剧情与导入校验仍采用共享核心规则。</summary>
public interface IChapterStorage
{
    IReadOnlyList<InstalledPackage> List();
    InstalledPackage? Find(string packId);
    Pack Load(string packId);
    Task<InstalledPackage> ImportAsync(Stream zipStream, CancellationToken cancellationToken = default,
        IProgress<PackageImportProgress>? progress = null);
    void CleanAbandonedImports();
    Task<PackageRemovalResult> RemoveAsync(string packId, string expectedRevision);
    PackageFolder Browse(string packId, string relativePath = "", string? expectedRevision = null);
}
