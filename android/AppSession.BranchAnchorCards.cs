namespace PgrVoice.AndroidApp;

public sealed partial class AppSession
{
    bool ShowManualBranchAnchorCards(ManualBranchConfirmation confirmation)
    {
        var offer = BranchAnchorPolicy.Create(confirmation.Engine, maximumLinesPerOption: 2);
        if (offer.Cards.Count == 0) return false;
        overlay.ShowManualBranchSelection(BrowseSectionTitle(confirmation.Engine, confirmation.Menu.SectionId),
            offer.Cards.Select(c => c.Speaker + "：" + c.Text + (c.IsAmbiguous ? "\n同文：请按选项继续核对" : "")).ToArray(),
            index => main.PostDelayed(() => Post(() =>
            {
                if (!ReferenceEquals(manualBranchConfirmation, confirmation) || !ManualBranchEnvironmentValid(confirmation) || index < 0 || index >= offer.Cards.Count) return;
                if (!BranchAnchorPolicy.TryResolve(confirmation.Engine, offer, offer.Cards[index].Id, out var node, out var reason) || node == null)
                { Status = reason; Notify(); BeginManualBranchFollow(confirmation.Engine, confirmation.Target, confirmation.DisplayKey, confirmation.ResumeAutoAfterCommon, showOptionLabels: true); return; }
                if (!CanAutoPlayLine(confirmation.Engine.Pack, node))
                { Status = "这句暂时没有可用配音，请对照游戏手动定位。"; Notify(); return; }
                CancelManualBranchFollow();
                var engine = confirmation.Engine;
                ConfirmLine(node.Id);
                if (ReferenceEquals(Engine, engine) && engine.CurrentId == node.Id && engine.Mode == RunMode.Following)
                {
                    StartClickFollow(preserveAudio: true);
                    RecordBranchAutoReturn(engine, confirmation.ResumeAutoAfterCommon);
                }
                Diagnostics.Log("玩家确认选后台词", confirmation.Menu.Id + " → " + node.Id);
            }), 180),
            () => Post(() =>
            {
                if (!ReferenceEquals(manualBranchConfirmation, confirmation)) return;
                CancelManualBranchFollow(); Status = "已取消台词核对，原位置保持。"; Notify();
            }), title: "此处分支请开启点按跟随",
            instruction: "先在游戏中选择，再点与画面一致的下一句，确认并开启点按跟随。从这句播放，无需再到悬浮控制开启。",
            buttonPrefix: "", secondaryActionLabel: "按选项核对",
            secondaryAction: () => main.PostDelayed(() => Post(() =>
            {
                if (ReferenceEquals(manualBranchConfirmation, confirmation) && ManualBranchEnvironmentValid(confirmation))
                    BeginManualBranchFollow(confirmation.Engine, confirmation.Target, confirmation.DisplayKey, confirmation.ResumeAutoAfterCommon, showOptionLabels: true);
            }), 180), anchorCards: true,
            additionalActionLabel: "游戏选好后，识别下一句",
            additionalAction: () => main.PostDelayed(() => Post(() => RequestManualBranchAnchor(confirmation)), 180));
        Status = "已到分支，请先在游戏中选择，再确认并开启点按跟随。点顶部台词卡即可，也可按选项核对。"; Notify();
        return true;
    }
}
