namespace PgrVoice.AndroidApp;

public enum OnboardingStatus { NotStarted, InProgress, Dismissed, Skipped, Completed }
public enum OnboardingStep { Purpose, Package, Trial, Finish }
public enum OnboardingPurpose { Listening, Game }

/// <summary>只保存教学选择，不保存或更改正式播放位置。试听成功由当次播放对象验证。</summary>
public sealed class OnboardingProgress
{
    public OnboardingStatus Status { get; set; }
    public OnboardingStep Step { get; set; }
    public OnboardingPurpose Purpose { get; set; }
    public string? PackId { get; set; }
    public string? ChapterId { get; set; }
    public string? NodeId { get; set; }
    public bool HeardConfirmed { get; set; }
    public long Epoch { get; set; }
    public string? PendingExternal { get; set; }
    public long ExternalEpoch { get; set; }
    public int ExternalRequestCode { get; set; }
    public static bool ShouldAutoStart(bool existingSettings, bool existingPackages, bool legacyShown, OnboardingStatus status) =>
        !existingSettings && !existingPackages && !legacyShown && status == OnboardingStatus.NotStarted;
    public void Begin()
    {
        if (Status == OnboardingStatus.Completed) { Step = OnboardingStep.Purpose; HeardConfirmed = false; }
        Epoch++; PendingExternal = null;
        Status = OnboardingStatus.InProgress;
    }
    public void SelectPurpose(OnboardingPurpose purpose)
    { Epoch++; PendingExternal = null; Purpose = purpose; Step = OnboardingStep.Package; HeardConfirmed = false; }
    public void SelectLine(string packId, string chapterId, string nodeId)
    { Epoch++; PendingExternal = null; PackId = packId; ChapterId = chapterId; NodeId = nodeId; HeardConfirmed = false; Step = OnboardingStep.Trial; }
    public bool ConfirmHeard(bool validPreview)
    {
        if (Status != OnboardingStatus.InProgress || Step != OnboardingStep.Trial || !validPreview) return false;
        HeardConfirmed = true; Step = OnboardingStep.Finish; return true;
    }
    public bool Complete()
    {
        if (Status != OnboardingStatus.InProgress || Step != OnboardingStep.Finish || !HeardConfirmed) return false;
        Status = OnboardingStatus.Completed; return true;
    }
    public void Exit(bool skip)
    { Status = skip ? OnboardingStatus.Skipped : OnboardingStatus.Dismissed; Epoch++; PendingExternal = null; }
    public long BeginExternal(string kind)
    {
        Epoch++; PendingExternal = kind; ExternalEpoch = Epoch;
        ExternalRequestCode = 0x4000 + (int)(Epoch % 8192) * 2 + (kind == "overlay" ? 1 : 0);
        return Epoch;
    }
    public bool AcceptExternal(string kind, long epoch) => Status == OnboardingStatus.InProgress &&
        Epoch == epoch && ExternalEpoch == epoch && PendingExternal == kind;
    public void EndExternal() => PendingExternal = null;
    public void Back()
    {
        Epoch++; PendingExternal = null;
        if (Step > OnboardingStep.Purpose) Step--;
        HeardConfirmed = false;
    }
}
