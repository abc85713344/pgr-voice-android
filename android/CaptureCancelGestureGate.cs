namespace PgrVoice.AndroidApp;

public enum CaptureCancelGestureAction { Down, Move, Up, PointerChanged, Cancel, DirectClick }

/// <summary>
/// 取消定位只接受本轮控件显示后开始的完整单指触摸。时钟统一使用 uptime；
/// 逻辑轮与窗口重建分开，重建后必须重新显示并从新的 Down 开始。
/// </summary>
public sealed class CaptureCancelGestureGate
{
    long generation, shownAt, downAt, lastEventAt;
    int pointerId;
    bool active, shown, tracking, consumed;
    public long Generation => generation;

    public long Begin()
    {
        generation++;
        active = true;
        consumed = false;
        ResetPresentation();
        return generation;
    }

    public void End()
    {
        active = false;
        ResetPresentation();
    }

    public void ResetPresentation()
    {
        shown = tracking = false;
        shownAt = downAt = lastEventAt = 0;
        pointerId = -1;
    }

    public bool MarkShown(long currentGeneration, long uptime)
    {
        if (!active || consumed || currentGeneration != generation || shown || uptime < 0) return false;
        shownAt = uptime;
        shown = true;
        return true;
    }

    public bool Handle(long currentGeneration, CaptureCancelGestureAction action,
        int pointer, int pointerCount, long downTime, long eventTime, bool inside, out string reason)
    {
        if (!active || currentGeneration != generation) { reason = "inactive-or-old-generation"; return false; }
        if (action == CaptureCancelGestureAction.DirectClick) { reason = "direct-click-without-touch"; return false; }
        if (!shown) { reason = "not-shown"; return false; }
        if (consumed) { reason = "already-consumed"; return false; }
        if (downTime < shownAt || eventTime < downTime)
        { reason = "touch-predates-display"; return false; }

        if (action == CaptureCancelGestureAction.Down)
        {
            bool duplicate = tracking && downAt == downTime;
            tracking = false;
            if (duplicate || pointerCount != 1 || pointer < 0 || !inside)
            { reason = duplicate ? "duplicate-down" : "invalid-down"; return false; }
            pointerId = pointer;
            downAt = downTime;
            lastEventAt = eventTime;
            tracking = true;
            reason = "tracking-new-touch";
            return false;
        }

        if (!tracking) { reason = "no-matching-down"; return false; }
        // 旧事件不能破坏另一次已经按下的有效触摸。
        if (downTime != downAt) { reason = "different-down-time"; return false; }
        if (action == CaptureCancelGestureAction.Cancel || action == CaptureCancelGestureAction.PointerChanged || pointerCount != 1)
        { tracking = false; reason = "cancelled-or-multitouch"; return false; }
        if (pointer != pointerId || eventTime < lastEventAt || !inside)
        { tracking = false; reason = "pointer-time-or-bounds-changed"; return false; }
        lastEventAt = eventTime;
        if (action == CaptureCancelGestureAction.Move) { reason = "tracking"; return false; }
        if (action != CaptureCancelGestureAction.Up)
        { tracking = false; reason = "unsupported-event"; return false; }
        tracking = false;
        consumed = true;
        reason = "accepted-complete-new-touch";
        return true;
    }
}
