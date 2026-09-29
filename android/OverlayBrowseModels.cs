namespace PgrVoice.AndroidApp;

public enum OverlayBrowsePage { Lines, Locate, Branches, History }

public sealed record OverlayBrowseItem(string Id, string Title, string Detail, bool Selected = false,
    bool RequiresConfirmation = true, string ConfirmationTitle = "确认当前位置", string ConfirmationText = "", bool IsDialogue = false);

public sealed record OverlayBrowseModel(OverlayBrowsePage Page, long Revision, string Title, string Hint,
    IReadOnlyList<OverlayBrowseItem> Items, IReadOnlyList<OverlayBrowseItem>? Actions = null);
