namespace PgrVoice.AndroidApp.Ocr;

public readonly record struct OcrScheduleTicket(long Id, long Generation, long Epoch, string FrameKey, bool Manual);

/// <summary>
/// 仅在会话主线程使用。识别频率按开始时间限制；同一稳定画面完成两次有效观察后休眠，
/// 但剧情核心仍有待确认结果时必须继续。只负责调度，不代替路线、置信度或双次确认规则。
/// </summary>
public sealed class OcrInferenceSchedule
{
    long generation, nextId, activeId, epoch;
    long? lastStartedAt;
    string? frameKey;
    int completedObservations;
    bool suppressed;
    public bool Slow { get; private set; }

    public void Reset()
    {
        generation++;
        frameKey = null; completedObservations = 0; suppressed = false; Slow = false;
        // 旧任务仍占用槽位，直到它的主线程回调交还；跨模式切换也不突破开始时间限速。
    }

    public bool TryBegin(long now, long currentEpoch, string currentFrameKey, bool stable, bool manual,
        bool lowPower, out OcrScheduleTicket ticket)
    {
        ticket = default;
        ArgumentException.ThrowIfNullOrEmpty(currentFrameKey);
        if (epoch != currentEpoch || frameKey != currentFrameKey)
        {
            generation++;
            epoch = currentEpoch; frameKey = currentFrameKey;
            completedObservations = 0; suppressed = false;
        }
        int interval = lowPower || Slow ? 1000 : 500;
        if (activeId != 0 || !manual && (!stable || suppressed) ||
            lastStartedAt is { } started && now - started < interval) return false;
        activeId = ++nextId; lastStartedAt = now;
        ticket = new(activeId, generation, epoch, currentFrameKey, manual);
        return true;
    }

    public bool Complete(OcrScheduleTicket ticket, bool validObservation, bool pendingConfirmation, double elapsedMilliseconds)
    {
        if (!IsCurrent(ticket)) { Abandon(ticket); return false; }
        activeId = 0;
        if (double.IsFinite(elapsedMilliseconds)) Slow = elapsedMilliseconds > 500;
        if (!validObservation)
        {
            completedObservations = 0; suppressed = false;
        }
        else if (!ticket.Manual)
        {
            completedObservations = Math.Min(2, completedObservations + 1);
            suppressed = completedObservations >= 2 && !pendingConfirmation;
        }
        return true;
    }

    public void Fail(OcrScheduleTicket ticket)
    {
        if (IsCurrent(ticket)) { completedObservations = 0; suppressed = false; }
        Abandon(ticket);
    }

    public void Abandon(OcrScheduleTicket ticket)
    {
        if (activeId == ticket.Id) activeId = 0;
    }

    bool IsCurrent(OcrScheduleTicket ticket) => activeId == ticket.Id && ticket.Generation == generation &&
        ticket.Epoch == epoch && ticket.FrameKey == frameKey;
}
