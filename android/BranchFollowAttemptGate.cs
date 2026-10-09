namespace PgrVoice.AndroidApp;

public enum BranchFollowStage { Inactive, ReadingOptions, AwaitingChoice, Dispatching, ReadingLine }

public readonly record struct BranchFollowObservationTicket(long Generation, BranchFollowStage Stage, long SourceFrameId);

/// <summary>普通二选一的短时会话门禁；任何旧观察、重复触摸或过期回调都不能提交路线。</summary>
public sealed class BranchFollowAttemptGate
{
    long generation, stageStarted, lastSourceFrame, nextObservation;
    bool observationInFlight;
    public long Generation => generation;
    public BranchFollowStage Stage { get; private set; }
    public long Deadline { get; private set; }
    public bool IsActive => Stage != BranchFollowStage.Inactive;
    public bool IsExpired(long now) => IsActive && now >= Deadline;

    public void Start(long now) => ChangeStage(BranchFollowStage.ReadingOptions, now, 25_000);
    // 玩家自己选择游戏选项；只观察首句，绝不进入选项触碰阶段。
    public void StartDefaultLine(long now) => ChangeStage(BranchFollowStage.ReadingLine, now, 90_000);

    public bool TryBeginObservation(long now, long sourceFrameId, long sourceTimestampMs, bool isRepeated,
        out BranchFollowObservationTicket ticket)
    {
        ticket = default;
        if (Stage is not (BranchFollowStage.ReadingOptions or BranchFollowStage.ReadingLine) || IsExpired(now) ||
            observationInFlight || now < nextObservation || isRepeated || sourceFrameId <= lastSourceFrame ||
            sourceTimestampMs < stageStarted || sourceTimestampMs > now || now - sourceTimestampMs > 1_500) return false;
        lastSourceFrame = sourceFrameId; nextObservation = now + 550; observationInFlight = true;
        ticket = new(generation, Stage, sourceFrameId); return true;
    }

    public bool CompleteObservation(BranchFollowObservationTicket ticket, long now)
    {
        if (!observationInFlight || ticket.Generation != generation || ticket.Stage != Stage ||
            ticket.SourceFrameId != lastSourceFrame || IsExpired(now)) return false;
        observationInFlight = false; return true;
    }

    public bool Arm(long now)
    {
        if (Stage != BranchFollowStage.ReadingOptions || observationInFlight || IsExpired(now)) return false;
        ChangeStage(BranchFollowStage.AwaitingChoice, now, 15_000); return true;
    }

    public bool TryBeginTap(long now)
    {
        if (Stage != BranchFollowStage.AwaitingChoice || IsExpired(now)) return false;
        ChangeStage(BranchFollowStage.Dispatching, now, 5_000); return true;
    }

    public bool CompleteTap(long expectedGeneration, bool completed, long now)
    {
        if (expectedGeneration != generation || Stage != BranchFollowStage.Dispatching || IsExpired(now)) return false;
        if (!completed) { Stop(); return false; }
        ChangeStage(BranchFollowStage.ReadingLine, now, 25_000); return true;
    }

    public void Stop() => ChangeStage(BranchFollowStage.Inactive, 0, 0);

    void ChangeStage(BranchFollowStage stage, long now, long timeout)
    {
        generation++; Stage = stage; stageStarted = now; Deadline = now + timeout;
        observationInFlight = false; lastSourceFrame = 0; nextObservation = now;
    }
}
