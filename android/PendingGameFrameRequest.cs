namespace PgrVoice.AndroidApp;

public enum GameFrameRequestResult { None, Waiting, Ready, Expired }

/// <summary>
/// 用户主动取图的一次请求。等待播放器进入后台和显示形态稳定后才取帧，
/// 不把发起时的播放器方向当作游戏方向。由主线程调用；不保存截图或修改剧情。
/// </summary>
public sealed class PendingGameFrameRequest<T> where T : class
{
    const long TimeoutMilliseconds = 10_000;
    const long StableMilliseconds = 700;
    T? pending;
    long session, requestedAt, stableSince;
    ScreenDisplayState? stableDisplay;
    int stableWidth, stableHeight, samples;

    public void Set(long sessionId, long now, T value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionId);
        ArgumentOutOfRangeException.ThrowIfNegative(now);
        ArgumentNullException.ThrowIfNull(value);
        Clear();
        session = sessionId;
        requestedAt = now;
        pending = value;
    }

    public bool IsPendingFor(long sessionId) => pending != null && session == sessionId;

    /// <summary>
    /// now 使用与 Set 相同的单调时钟；每次有效画面采样调用一次。
    /// 静态画面的重复采样可用于等待稳定。会话变化取消并返回 None；
    /// 只有 Ready 返回请求值，调用方此时再将截图绑定到当前显示与尺寸。
    /// </summary>
    public GameFrameRequestResult Poll(long now, long sessionId, ScreenDisplayState display,
        int width, int height, bool uiVisible, out T? request)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(now);
        ArgumentNullException.ThrowIfNull(display);
        request = null;
        if (pending == null) return GameFrameRequestResult.None;
        if (sessionId != session) { Clear(); return GameFrameRequestResult.None; }
        // 显示形态变化和前后台切换均不得延长总期限。
        if (now - requestedAt >= TimeoutMilliseconds)
        { Clear(); return GameFrameRequestResult.Expired; }
        if (uiVisible || display.RegionKey == null || width <= 0 || height <= 0)
        { ResetStability(); return GameFrameRequestResult.Waiting; }
        if (stableDisplay != display || stableWidth != width || stableHeight != height || now < stableSince)
        {
            stableDisplay = display;
            stableWidth = width;
            stableHeight = height;
            stableSince = now;
            samples = 1;
            return GameFrameRequestResult.Waiting;
        }
        samples = Math.Min(2, samples + 1);
        if (samples < 2 || now - stableSince < StableMilliseconds)
            return GameFrameRequestResult.Waiting;
        request = pending;
        Clear();
        return GameFrameRequestResult.Ready;
    }

    public void Clear()
    {
        pending = null;
        session = requestedAt = 0;
        ResetStability();
    }

    void ResetStability()
    {
        stableDisplay = null;
        stableWidth = stableHeight = samples = 0;
        stableSince = 0;
    }
}
