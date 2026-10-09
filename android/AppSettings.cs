using PgrVoice.AndroidApp.Ocr;

namespace PgrVoice.AndroidApp;

public sealed record ScreenRegion(float Left = .025f, float Top = .64f, float Width = .95f, float Height = .34f)
{
    public ScreenRegion Clamp() => new(Math.Clamp(Left, 0, .95f), Math.Clamp(Top, 0, .95f),
        Math.Clamp(Width, .05f, 1 - Math.Clamp(Left, 0, .95f)), Math.Clamp(Height, .05f, 1 - Math.Clamp(Top, 0, .95f)));
}

public sealed class AppSettings
{
    public string? LastPackId { get; set; }
    // 章节目录的分类选择独立于正在播放的章节。
    public string? ChapterCategory { get; set; }
    public Dictionary<string, ImportedZipSource> ImportedArchives { get; set; } = new();
    // 仅控制播放器主界面，不改变系统或游戏的屏幕方向。
    public string MainOrientation { get; set; } = "auto";
    public bool ShowCompactButtons { get; set; } = true;
    public bool ShowCompactControls { get; set; } = true;
    // 常规人工分支跟随偏好；默认分支自动续播由下方独立开关控制。
    public bool BranchAutoFollow { get; set; }
    // 新试验流程：只在选择点暂停，玩家确认选后对白后沿已选路线继续自动播放。
    public bool AutoPlayConfirmBranch { get; set; } = true;
    // 只覆盖已启动的自动播放分支；升级不自动开启，也不改变手动/点按跟随的偏好。
    public bool AutoPlayDefaultBranchEnabled { get; set; }
    public int DefaultBranchOption { get; set; } = 1;
    // 旧版持续 OCR 跟随设置，仅兼容读取历史设置，不再影响定位。
    public bool LowPower { get; set; }
    public bool VoicePriority { get; set; }
    public float Volume { get; set; } = .85f;
    public Dictionary<string, int> SpeakerVolumes { get; set; } = new(StringComparer.Ordinal);
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.Paddle;
    public bool OcrFullScreen { get; set; } = true;
    public Dictionary<string, ScreenRegion> Regions { get; set; } = new();
    // Regions 是旧版四类比例键，仅保留原数据。无法可靠推断它来自内屏还是外屏，不自动迁移。
    public Dictionary<string, ScreenRegion> DisplayRegions { get; set; } = new();
    public Dictionary<string, AdvanceTapTarget> AdvanceTargets { get; set; } = new();
    // 旧文件缺字段会使用初值；损坏文件中的显式 null 也不能让设置入口失效。
    public void Normalize()
    {
        ImportedArchives ??= new();
        SpeakerVolumes ??= new(StringComparer.Ordinal);
        Regions ??= new();
        DisplayRegions ??= new();
        AdvanceTargets ??= new();
    }
    public AdvanceTapTarget? AdvanceTargetFor(string displayKey) => AdvanceTargets.GetValueOrDefault(displayKey);
    public void SetAdvanceTarget(string displayKey, AdvanceTapTarget target) =>
        AdvanceTargets[displayKey] = target;
#if DEBUG
    public string? DebugAutoPlayPackage { get; set; }
#endif
    public bool OnboardingShown { get; set; }
    public bool HasRegion(string? displayKey) => displayKey != null && DisplayRegions.ContainsKey(displayKey);
    public ScreenRegion RegionFor(string? displayKey) =>
        displayKey != null && DisplayRegions.TryGetValue(displayKey, out var region) ? region.Clamp() : new();
}
