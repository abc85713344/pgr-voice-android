using Android.OS;
using PgrVoice.AndroidApp.Platform;
using System.Text.Json;

namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    sealed record BranchAnchorRequest(PlaybackEngine Engine, string MenuId, string SectionId, long Epoch,
        long AudioGeneration, long CaptureSession, ScreenDisplayState Display, string Navigation,
        BranchResumeIntent? ResumeIntent);
    BranchAnchorRequest? branchAnchorRequest;
    long branchAnchorServiceToken;

    void CancelBranchAnchorRequest()
    {
        branchAnchorRequest = null;
        bool hadSession = branchAnchorServiceToken != 0; branchAnchorServiceToken = 0;
        if (hadSession) GameAdvanceAccessibilityService.CancelSession("单次分支定位已取消。");
    }

    void RequestManualBranchAnchor(ManualBranchConfirmation confirmation)
    {
        if (!ReferenceEquals(manualBranchConfirmation, confirmation) || !ManualBranchEnvironmentValid(confirmation)) return;
        var intent = new BranchResumeIntent(confirmation.Engine, confirmation.Menu.Id, confirmation.GamePackage,
            confirmation.Target, confirmation.DisplayKey, SystemClock.ElapsedRealtime(), confirmation.ResumeAutoAfterCommon);
        RequestBranchAnchorOcr(intent);
    }

    public void RequestBranchAnchorOcr() => RequestBranchAnchorOcr(null);

    void RequestBranchAnchorOcr(BranchResumeIntent? intent)
    {
        var engine = Engine;
        if (engine?.Current is not { Kind: "choice" } menu)
        { RequestOcr(true); return; }
        if (!Screen.CaptureActive)
        {
            Invalidate(); audio.Stop(); engine.PauseForBrowse();
            Status = "请先授权屏幕捕获，再回游戏选好并重新点击“识别下一句”。";
            Notify(); OpenApp("Capture"); return;
        }
        RequestOcr(true);
        // 授权会切回应用；授权之后需要玩家重新发起，不保存可自动恢复的旧意图。
        if (!Screen.CaptureActive || !ReferenceEquals(Engine, engine)) return;
        var request = new BranchAnchorRequest(engine, menu.Id, menu.SectionId, ocrEpoch, playGeneration,
            Screen.SessionId, Screen.DisplayState, JsonSerializer.Serialize(engine.ExportNavigation(), Json.Options), intent);
        if (intent != null)
        {
            if (!BranchIntentEnvironmentValid(intent)) { Invalidate(); Status = "游戏或屏幕已变化，请重新确认游戏中的下一句。"; Notify(); return; }
            branchAnchorServiceToken = GameAdvanceAccessibilityService.BeginSession(intent.GamePackage,
                intent.Target.DisplayWidth, intent.Target.DisplayHeight, intent.Target.Rotation);
            if (branchAnchorServiceToken == 0) { Invalidate(); Status = GameAdvanceAccessibilityService.LastFailureReason; Notify(); return; }
        }
        branchAnchorRequest = request;
        Status = "请先在游戏里选好，等下一句完整出现。正在进行本次定位；唯一已核开头台词会播放一次后恢复点按，其余请核对候选。";
        Notify();
    }

    bool BranchAnchorCurrent(BranchAnchorRequest request) => ReferenceEquals(branchAnchorRequest, request) &&
        ReferenceEquals(Engine, request.Engine) && request.Engine.CurrentId == request.MenuId && request.Engine.Mode == RunMode.Choice &&
        ocrEpoch == request.Epoch && playGeneration == request.AudioGeneration && SectionId == request.SectionId &&
        !uiVisible && Screen.CaptureActive && Screen.SessionId == request.CaptureSession && Screen.DisplayState == request.Display &&
        JsonSerializer.Serialize(request.Engine.ExportNavigation(), Json.Options) == request.Navigation &&
        (request.ResumeIntent == null || BranchIntentEnvironmentValid(request.ResumeIntent));

    // 单次 OCR 的唯一消费点。只核输入证据，截图/OCR 不在主机测试中伪装为真实设备识别。
    bool TryHandleBranchAnchorResult(IReadOnlyList<OcrBlock> blocks, int width, int height, bool truncated, out string? notice)
    {
        notice = null;
        if (branchAnchorRequest is not { } request) return false;
        bool current = BranchAnchorCurrent(request);
        CancelBranchAnchorRequest();
        if (!current) { Candidates = Array.Empty<MatchCandidate>(); notice = "本次定位期间游戏、菜单或屏幕已变化，请重新识别。"; return false; }
        if (truncated) { notice = "识别文字不完整，请重试或对照游戏手动确认候选。"; return false; }
        // 没有本次原跟随环境时仍走现有候选确认，不因一个普通定位请求自动发声。
        if (request.ResumeIntent is not { } intent) { notice = "请核对选后下一句和路线，再采用候选。"; return false; }
        if (!BranchFollowPolicy.TryMatchMenuLine(request.Engine, request.MenuId, blocks, width, height, out var nodeId, out var reason))
        { notice = reason; return false; }
        var engine = request.Engine;
        var probe = new PlaybackEngine(engine.Pack);
        if (!probe.ImportNavigation(engine.ExportNavigation()) || !probe.ConfirmGameLine(nodeId!) ||
            probe.Mode != RunMode.Following || probe.CurrentId != nodeId || !CanAutoPlayLine(engine.Pack, engine.Pack.ById[nodeId!]))
        { notice = "这句尚不能可靠续接或没有可用配音，请对照游戏确认候选。"; return false; }
        if (!BranchIntentEnvironmentValid(intent)) { notice = "游戏或屏幕已变化，请重新定位。"; return false; }
        overlay.SetCaptureHidden(false);
        // 正式导航只确认当前锚句一次；不先选支、不补Next、不跳过第一句。
        ConfirmLine(nodeId!);
        if (ReferenceEquals(Engine, engine) && engine.CurrentId == nodeId && engine.Mode == RunMode.Following && BranchIntentEnvironmentValid(intent))
        { StartClickFollow(preserveAudio: true); RecordBranchAutoReturn(engine, intent.ResumeAutoAfterCommon); }
        Diagnostics.Log("选后正文单次定位", request.MenuId + " → " + nodeId);
        return true;
    }
}
