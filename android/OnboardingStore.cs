namespace PgrVoice.AndroidApp;

/// <summary>必须在读取设置、创建仓库或 ApplyAudioSettings 之前取样。</summary>
public sealed record OnboardingInstallSnapshot(bool ExistingSettings, bool ExistingPackages)
{
    public static OnboardingInstallSnapshot Capture(string root)
    {
        bool settings = File.Exists(Path.Combine(root, "settings.json")) || File.Exists(Path.Combine(root, "settings.json.bak"));
        if(settings)return new(true,false); // 已确定为旧安装，无需扫描庞大的音频目录。
        string chapters = Path.Combine(root, "chapters");
        bool packages;
        try { packages = Directory.Exists(chapters) && Directory.EnumerateFiles(chapters, "pack.json", SearchOption.AllDirectories).Any(); }
        catch { packages = true; } // 无法安全判断的既有库不当成全新安装。
        return new(settings, packages);
    }
}

public sealed class OnboardingStore
{
    readonly string file;
    public OnboardingProgress Progress { get; }
    public bool OfferAutomatically { get; private set; }
    public OnboardingStore(string root, OnboardingInstallSnapshot installed, bool legacyShown)
    {
        file = Path.Combine(root, "onboarding.json");
        try { Progress = Json.ReadWithBackup<OnboardingProgress>(file, out _); }
        catch { Progress = new(); }
        OfferAutomatically = OnboardingProgress.ShouldAutoStart(installed.ExistingSettings,
            installed.ExistingPackages, legacyShown, Progress.Status);
    }
    public void Begin()
    { OfferAutomatically = false; Progress.Begin(); Save(); }
    public void Save() => Json.Save(file, Progress);
    public void Exit(bool skip) { OfferAutomatically = false; Progress.Exit(skip); Save(); }
}
