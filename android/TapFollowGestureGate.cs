namespace PgrVoice.AndroidApp;

public readonly record struct TapFollowPoint(float X, float Y);
public readonly record struct TapFollowRequestTicket(long Epoch, long Id);

/// <summary>只识别物理坐标中的单指轻点，不生成系统触摸，也不改变剧情。</summary>
public sealed class TapFollowGestureGate
{
    readonly double maximumDistanceSquared;
    readonly long maximumHoldMs, debounceMs;
    bool tracking;
    int pointer;
    float startX, startY;
    long downAt;
    long? lastAcceptedAt;

    public TapFollowGestureGate(double maximumDistancePixels, long maximumHoldMs = 700, long debounceMs = 300)
    {
        if (!double.IsFinite(maximumDistancePixels) || maximumDistancePixels <= 0 || maximumHoldMs <= 0 || debounceMs < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDistancePixels));
        maximumDistanceSquared = maximumDistancePixels * maximumDistancePixels;
        this.maximumHoldMs = maximumHoldMs; this.debounceMs = debounceMs;
    }

    public bool Begin(int pointerId, int pointerCount, float rawX, float rawY, long timeMs)
    {
        if (tracking) { Cancel(); return false; }
        if (pointerId < 0 || pointerCount != 1 || !CoordinatesValid(rawX, rawY) || timeMs < 0) return false;
        tracking = true; pointer = pointerId; startX = rawX; startY = rawY; downAt = timeMs;
        return true;
    }

    public void Move(int pointerId, int pointerCount, float rawX, float rawY, long timeMs)
    {
        if (tracking && !ValidPoint(pointerId, pointerCount, rawX, rawY, timeMs)) Cancel();
    }

    public bool TryEnd(int pointerId, int pointerCount, float rawX, float rawY, long timeMs, out TapFollowPoint point)
    {
        point = default;
        bool valid = tracking && ValidPoint(pointerId, pointerCount, rawX, rawY, timeMs);
        Cancel();
        if (!valid || lastAcceptedAt is { } last && (timeMs < last || timeMs - last < debounceMs)) return false;
        lastAcceptedAt = timeMs; point = new(rawX, rawY);
        return true;
    }

    /// <summary>拖动、多指、窗口撤销或系统取消后调用；保留去重时钟，避免重挂窗口绕过去重。</summary>
    public void Cancel() => tracking = false;

    bool ValidPoint(int pointerId, int pointerCount, float x, float y, long timeMs)
    {
        if (pointerId != pointer || pointerCount != 1 || !CoordinatesValid(x, y) || timeMs < downAt || timeMs - downAt > maximumHoldMs)
            return false;
        double dx = (double)x - startX, dy = (double)y - startY;
        return dx * dx + dy * dy <= maximumDistanceSquared;
    }

    static bool CoordinatesValid(float x, float y) => float.IsFinite(x) && float.IsFinite(y) && x >= 0 && y >= 0;
}

/// <summary>
/// 串行会话队列上的注入请求票据。停止、切换模式或显示形态改变时 Invalidate；
/// 为当前注入临时撤下触摸窗口时不要 Invalidate。只有有效成功完成可推进一次。
/// </summary>
public sealed class TapFollowRequestGate
{
    readonly long debounceMs;
    long nextId, activeId;
    long? lastStartedAt;
    public long Epoch { get; private set; }
    public bool IsInFlight => activeId != 0;

    public TapFollowRequestGate(long debounceMs = 300)
    {
        if (debounceMs < 0) throw new ArgumentOutOfRangeException(nameof(debounceMs));
        this.debounceMs = debounceMs;
    }

    public bool TryBegin(long nowMs, out TapFollowRequestTicket ticket)
    {
        ticket = default;
        if (nowMs < 0 || IsInFlight || lastStartedAt is { } last && (nowMs < last || nowMs - last < debounceMs)) return false;
        activeId = checked(++nextId); lastStartedAt = nowMs;
        ticket = new(Epoch, activeId);
        return true;
    }

    public bool IsCurrent(TapFollowRequestTicket ticket) => activeId != 0 && ticket.Epoch == Epoch && ticket.Id == activeId;

    /// <summary>返回是否核销当前请求；mayAdvance 只有当前请求成功时为真。失败也释放占位。</summary>
    public bool TryComplete(TapFollowRequestTicket ticket, bool succeeded, out bool mayAdvance)
    {
        mayAdvance = false;
        if (!IsCurrent(ticket)) return false;
        activeId = 0; mayAdvance = succeeded;
        return true;
    }

    /// <summary>便捷形式只在成功且首次核销时返回 true；需要区分失败与过期回调时用三参数形式。</summary>
    public bool TryComplete(TapFollowRequestTicket ticket, bool succeeded) => TryComplete(ticket, succeeded, out bool advance) && advance;

    public void Invalidate() { Epoch = checked(Epoch + 1); activeId = 0; }
}
