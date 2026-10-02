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
    public Dictionary<string, ImportedZipSource> ImportedArchives { get; set; } = new();
    // 仅控制播放器主界面，不改变系统或游戏的屏幕方向。
    public string MainOrientation { get; set; } = "auto";
    public bool ShowCompactButtons { get; set; } = true;
    public bool ShowCompactControls { get; set; } = true;
    // 旧版持续 OCR 跟随设置，仅兼容读取历史设置，不再影响定位。
    public bool LowPower { get; set; }
    public bool VoicePriority { get; set; }
    public float Volume { get; set; } = .85f;
    public OcrEngineKind OcrEngine { get; set; } = OcrEngineKind.Paddle;
    public bool OcrFullScreen { get; set; } = true;
    public Dictionary<string, ScreenRegion> Regions { get; set; } = new();
    // Regions 是旧版四类比例键，仅保留原数据。无法可靠推断它来自内屏还是外屏，不自动迁移。
    public Dictionary<string, ScreenRegion> DisplayRegions { get; set; } = new();
    public Dictionary<string, AdvanceTapTarget> AdvanceTargets { get; set; } = new();
#if DEBUG
    public string? DebugAutoPlayPackage { get; set; }
#endif
    public bool OnboardingShown { get; set; }
    public bool HasRegion(string? displayKey) => displayKey != null && DisplayRegions.ContainsKey(displayKey);
    public ScreenRegion RegionFor(string? displayKey) =>
        displayKey != null && DisplayRegions.TryGetValue(displayKey, out var region) ? region.Clamp() : new();
}
