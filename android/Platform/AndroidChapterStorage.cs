using PgrVoice.AndroidApp.Contracts;
using PgrVoice.Packages;

namespace PgrVoice.AndroidApp.Platform;

public sealed class AndroidChapterStorage : IChapterStorage
{
    private readonly PackageRepository repository;
    public AndroidChapterStorage(string root, PackageImportLimits? limits = null) => repository = new(root, limits);
    public IReadOnlyList<InstalledPackage> List() => repository.List();
    public InstalledPackage? Find(string packId) => repository.Find(packId);
    public Pack Load(string packId) => BundledRouteRepairs.Apply(repository.Load(packId));
    public Task<InstalledPackage> ImportAsync(Stream zipStream, CancellationToken cancellationToken = default,
        IProgress<PackageImportProgress>? progress = null) => repository.ImportAsync(zipStream, cancellationToken, progress);
    public void CleanAbandonedImports() => repository.CleanAbandonedImports();
    public Task<PackageRemovalResult> RemoveAsync(string packId, string expectedRevision) => repository.RemoveAsync(packId, expectedRevision);
    public PackageFolder Browse(string packId, string relativePath = "", string? expectedRevision = null) => repository.Browse(packId, relativePath, expectedRevision);
}
