namespace PgrVoice.AndroidApp;

/// <summary>来自物理显示模式；绝不能用 MediaProjection 输出大小代替这些尺寸。</summary>
public sealed record PhysicalDisplayProfile(int DisplayId, int PhysicalWidth, int PhysicalHeight, int Rotation)
{
    public bool IsValid => DisplayId >= 0 && PhysicalWidth > 0 && PhysicalHeight > 0 && Rotation is >= 0 and <= 3;
    // Android 的模式尺寸是自然方向，Rotation 使用 Surface.ROTATION_* 的四分之一圈编号。
    // 不包含刷新率/ModeId，避免 60/120 Hz 切换丢失同一屏幕的字幕框。
    public string? RegionKey => IsValid
        ? FormattableString.Invariant($"display-v2:{DisplayId}:{PhysicalWidth}x{PhysicalHeight}:axis-{Rotation % 2}")
        : null;
}

/// <summary>同一服务内，每次物理显示形态改变都增加版本，返回旧屏幕也不接受旧帧。</summary>
public sealed record ScreenDisplayState(PhysicalDisplayProfile? Profile, long Revision)
{
    public static ScreenDisplayState Unavailable { get; } = new(null, 0);
    public string? RegionKey => Profile?.RegionKey;
    public ScreenDisplayState WithProfile(PhysicalDisplayProfile? profile)
    {
        if (profile?.IsValid == false) profile = null;
        return Profile == profile ? this : new(profile, checked(Revision + 1));
    }
}

/// <summary>框选窗口固定绑定取图时的显示形态和授权会话；不能将旧图保存给新屏幕。</summary>
public sealed record RegionSelectionContext(ScreenDisplayState Display, long SessionId, int Width, int Height)
{
    public bool Matches(ScreenDisplayState display, long sessionId, int width, int height) =>
        Width > 0 && Height > 0 && Display.RegionKey != null && Display == display && SessionId == sessionId && Width == width && Height == height;
}
