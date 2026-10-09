using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Ui;
using PgrVoice.Listening;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    Pack? listeningBrowsePack;
    string? listeningBrowseChapter, listeningBrowseSection, listeningSelectedKey;
    bool listeningBrowsePinned, listeningRestoreScroll;
    int listeningBrowseTop, listeningBrowseOffset;
    IReadOnlyList<ListeningItem>? listeningDirectoryPlan;
    ListeningDirectory? listeningDirectory;
    LinearLayout? listeningDirectoryCard;
    ListView? listeningDirectoryList;
    ListeningDirectoryAdapter? listeningDirectoryAdapter;
    TextView? listeningDirectoryCount, listeningBrowseHint;
    Button? listeningSectionButton, listeningLocateButton, listeningPendingButton;
    string? listeningHighlightedItem;

    void ClearListeningDirectoryViews()
    {
        if (listeningDirectoryList != null)
        {
            listeningBrowseTop = listeningDirectoryList.FirstVisiblePosition;
            listeningBrowseOffset = listeningDirectoryList.GetChildAt(0)?.Top ?? 0;
            listeningRestoreScroll = true;
        }
        listeningDirectoryCard = null; listeningDirectoryList = null; listeningDirectoryAdapter = null;
        listeningDirectoryCount = listeningBrowseHint = null;
        listeningSectionButton = listeningLocateButton = listeningPendingButton = null;
        listeningDirectoryPlan = null;
        if (session.Listening.Pack == null)
        { listeningBrowsePack = null; listeningDirectory = null; listeningBrowseChapter = listeningBrowseSection = listeningSelectedKey = null; }
    }

    void BuildListeningDirectory()
    {
        var card = listeningDirectoryCard = Card(content);
        card.SetPadding(Dp(9), Dp(4), Dp(9), Dp(6));
        listeningSectionButton = Button(card, "小节台词", ShowListeningSections);
        listeningSectionButton.TextSize = 14; ButtonIcon(listeningSectionButton, "story");
        listeningDirectoryCount = Text("", 11); listeningDirectoryCount.SetTextColor(PgrTheme.Secondary); card.AddView(listeningDirectoryCount);
        listeningBrowseHint = Text("", 11); listeningBrowseHint.SetTextColor(PgrTheme.Cyan); card.AddView(listeningBrowseHint);
        var list = listeningDirectoryList = new ListView(this) { Divider = null, DividerHeight = Dp(6) };
        list.SetBackgroundColor(Color.Transparent); list.Selector = new ColorDrawable(Color.Transparent);
        list.ContentDescription = "小节完整台词列表，点选后用下方按钮定位";
        listeningDirectoryAdapter = new(this); list.Adapter = listeningDirectoryAdapter;
        int screenHeight = Resources!.Configuration!.ScreenHeightDp;
        int height = CompactLayout ? Math.Clamp(screenHeight / 2, 140, 210) : Math.Clamp(screenHeight / 3, 230, 330);
        card.AddView(list, new LinearLayout.LayoutParams(-1, Dp(height)));
        list.SetOnTouchListener(new ListeningListTouch(this));
        list.ItemClick += (_, args) => Safe(() =>
        {
            if (!ReferenceEquals(list, listeningDirectoryList) || listeningDirectory == null || args.Position >= listeningDirectory.Entries.Count) return;
            listeningBrowsePinned = true; listeningSelectedKey = listeningDirectory.Entries[args.Position].Key;
            UpdateListeningDirectory();
        });
        var actions = Row(card);
        listeningLocateButton = Button(actions, "定位到选中台词", () => LocateListeningDirectory(listeningSelectedKey));
        listeningLocateButton.TextSize = 12;
        var back = Button(actions, "回到正在听", () =>
        { listeningBrowsePinned = false; UpdateListeningDirectory(true); ScrollToListeningDirectory(); });
        back.TextSize = 12;
        var sectionActions = Row(card);
        var start = Button(sectionActions, "定位到小节开头", () =>
        {
            var player = session.Listening;
            if (!ListeningDirectoryOwnerMatches() || listeningBrowseSection == null) return;
            string target = listeningBrowseSection;
            player.JumpToSection(target);
            if (player.CurrentItem?.SectionId == target)
            { listeningBrowsePinned = false; UpdateListeningDirectory(true); ScrollToListeningDirectory(); OpenDirectoryChoices(); }
        });
        start.TextSize = 12;
        listeningPendingButton = Button(sectionActions, "打开本节待选互动", () => LocateListeningDirectory(listeningDirectory?.PendingKey));
        listeningPendingButton.TextSize = 12;
    }

    bool ListeningDirectoryOwnerMatches() => ReferenceEquals(listeningBrowsePack, session.Listening.Pack)
        && listeningBrowseChapter == session.Listening.Chapter?.Id;

    void UpdateListeningDirectory(bool scrollToSelection = false)
    {
        var player = session.Listening;
        if (listeningDirectoryList == null || player.Pack == null || player.Chapter == null) return;
        bool newOwner = !ListeningDirectoryOwnerMatches();
        if (newOwner)
        {
            listeningBrowsePack = player.Pack; listeningBrowseChapter = player.Chapter.Id;
            listeningBrowsePinned = false; listeningBrowseSection = listeningSelectedKey = null;
            listeningRestoreScroll = false;
        }
        string? currentSection = player.CurrentItem?.SectionId;
        if (!listeningBrowsePinned || !player.Chapter.Sections.Any(s => s.Id == listeningBrowseSection))
            listeningBrowseSection = currentSection ?? player.Chapter.Sections.FirstOrDefault()?.Id;
        if (listeningBrowseSection == null) return;
        bool changed = newOwner || !ReferenceEquals(listeningDirectoryPlan, player.Items)
            || listeningDirectory?.SectionId != listeningBrowseSection;
        if (changed)
        {
            listeningDirectoryPlan = player.Items;
            listeningDirectory = player.ReadDirectory(listeningBrowseSection);
        }
        if (listeningDirectory == null) return;
        var rows = listeningDirectory.Entries;
        bool currentChanged = listeningHighlightedItem != player.CurrentItem?.Id;
        listeningHighlightedItem = player.CurrentItem?.Id;
        if (!listeningBrowsePinned)
        {
            listeningSelectedKey = rows.FirstOrDefault(e => e.ItemId != null && e.ItemId == listeningHighlightedItem)?.Key;
            if (player.HasBlockingNotice && currentSection == listeningBrowseSection)
            {
                var previous = player.Items.TakeWhile(x => x.Id != player.CurrentItem?.Id)
                    .LastOrDefault(x => x.Kind == PgrVoice.Listening.ListeningItemKind.Line && x.SectionId == currentSection);
                listeningSelectedKey = rows.FirstOrDefault(e => previous != null && e.NodeId == previous.NodeId)?.Key
                    ?? rows.FirstOrDefault(e => e.IsPreview)?.Key;
            }
        }
        if (!rows.Any(e => e.Key == listeningSelectedKey)) listeningSelectedKey = rows.FirstOrDefault()?.Key;
        listeningDirectoryAdapter!.NotifyDataSetChanged();
        var section = player.Chapter.Sections.First(s => s.Id == listeningBrowseSection);
        string sectionTitle = SectionDisplay.Title(player.Chapter.Sections, section.Id);
        listeningSectionButton!.Text = "小节：" + sectionTitle + "  ▾";
        listeningDirectoryCount!.Text = $"完整正文 {listeningDirectory.LineCount} 条（含动作提示）· 浏览不播放";
        listeningBrowseHint!.Text = currentSection != null && currentSection != listeningBrowseSection
            ? "正在浏览：" + sectionTitle + "；收听仍在：" + SectionDisplay.Title(player.Chapter.Sections, player.CurrentItem!.SectionId)
            : player.HasBlockingNotice ? "当前路线等待确认。正文仍可阅读，未接入句可单句试听；目录顺序不代表游戏下一句。"
            : listeningBrowsePinned ? "浏览位置已保留；点“回到正在听”跟回当前句。" : "红色标记当前收听句，点台词可选中。";
        var selected = rows.FirstOrDefault(e => e.Key == listeningSelectedKey);
        listeningLocateButton!.Text = selected?.IsChoice == true ? "打开选中互动" : selected?.IsPreview == true
            ? "只试听选中台词" : "定位到选中台词";
        listeningLocateButton.Enabled = selected != null;
        listeningPendingButton!.Visibility = listeningDirectory.PendingItemId == null ? ViewStates.Gone : ViewStates.Visible;
        if (scrollToSelection || (!listeningBrowsePinned && (newOwner || changed || currentChanged)))
        {
            int index = rows.ToList().FindIndex(e => e.Key == listeningSelectedKey);
            ScrollListeningList(Math.Max(0, index), 0);
        }
        else if (listeningRestoreScroll) ScrollListeningList(listeningBrowseTop, listeningBrowseOffset);
        listeningRestoreScroll = false;
    }

    void ScrollListeningList(int position, int offset)
    {
        var list = listeningDirectoryList; string? section = listeningBrowseSection, selected = listeningSelectedKey;
        list?.Post(() =>
        {
            if (alive && ReferenceEquals(list, listeningDirectoryList) && section == listeningBrowseSection && selected == listeningSelectedKey)
                list!.SetSelectionFromTop(position, offset);
        });
    }

    void ScrollToListeningDirectory()
    {
        var card = listeningDirectoryCard;
        pageScroll.Post(() => { if (alive && ReferenceEquals(card, listeningDirectoryCard) && card != null) pageScroll.SmoothScrollTo(0, card.Top); });
    }

    void LocateListeningDirectory(string? key)
    {
        if (!ListeningDirectoryOwnerMatches() || key == null || listeningBrowseSection == null) return;
        var player = session.Listening;
        var entry = player.ReadDirectory(listeningBrowseSection)?.Entries.FirstOrDefault(x => x.Key == key);
        if (entry?.IsPreview == true)
        {
            var owner = player.Pack; string sectionId = listeningBrowseSection;
            player.Pause();
            Confirm("只试听这一句", entry.Speaker + "：" + entry.Text + "\n\n这句尚未接入当前路线。只播放选中的一句，结束后停住；原收听位置与选择保持。", () =>
            {
                if (!ReferenceEquals(owner, player.Pack) || !ListeningDirectoryOwnerMatches()) return;
                RequestNotifications(); player.PreviewDirectoryEntry(sectionId, key);
            });
            return;
        }
        if (!player.LocateDirectoryEntry(listeningBrowseSection, key))
        { Info("这句仅供阅读", player.Status); return; }
        listeningBrowsePinned = false; UpdateListeningDirectory(true); ScrollToListeningDirectory(); OpenDirectoryChoices();
    }

    void OpenDirectoryChoices()
    {
        var player = session.Listening;
        if (player.Choices.Count == 0) return;
        var owner = player.Pack; var plan = player.Items; string? currentId = player.CurrentItem?.Id;
        var choices = player.Choices.ToArray();
        Choose("待选互动 · 选择后继续收听", choices.Select(c => c.Label).ToArray(), i =>
        {
            if (!ReferenceEquals(owner, player.Pack) || !ReferenceEquals(plan, player.Items) || currentId != player.CurrentItem?.Id)
            { Info("收听位置已变化", "请重新打开当前互动选项。"); return; }
            RequestNotifications(); player.Choose(choices[i].Id);
        });
    }

    sealed class ListeningDirectoryAdapter(MainActivity owner) : BaseAdapter<ListeningDirectoryEntry>
    {
        public override int Count => owner.listeningDirectory?.Entries.Count ?? 0;
        public override ListeningDirectoryEntry this[int position] => owner.listeningDirectory!.Entries[position];
        public override long GetItemId(int position) => position;
        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var entry = this[position];
            var row = convertView as LinearLayout;
            if (row == null)
            {
                row = new LinearLayout(owner) { Orientation = Orientation.Vertical };
                row.SetPadding(owner.Dp(11), owner.Dp(6), owner.Dp(11), owner.Dp(7));
                foreach (int size in new[] { 12, 16, 11 })
                {
                    var text = owner.Text("", size); text.SetPadding(0, owner.Dp(2), 0, owner.Dp(2));
                    row.AddView(text, new LinearLayout.LayoutParams(-1, -2));
                }
            }
            bool selected = entry.Key == owner.listeningSelectedKey;
            bool current = entry.ItemId != null && entry.ItemId == owner.session.Listening.CurrentItem?.Id;
            row.Background = PgrTheme.Surface(owner, current ? PgrTheme.Raised : PgrTheme.Panel,
                selected ? PgrTheme.Cyan : PgrTheme.Border, current ? PgrTheme.Red : null);
            var heading = (TextView)row.GetChildAt(0)!;
            heading.Text = (entry.IsChoice ? "◆ " : entry.Number + " · ") + entry.Speaker + (current ? " · 当前收听" : "");
            heading.SetTextColor(current ? PgrTheme.Red : entry.IsChoice ? PgrTheme.Cyan : PgrTheme.Secondary);
            ((TextView)row.GetChildAt(1)!).Text = entry.Text;
            var note = (TextView)row.GetChildAt(2)!;
            note.Text = entry.IsPreview ? "未接入当前路线 · 可选中后只试听这句" : "";
            note.SetTextColor(PgrTheme.Secondary); note.Visibility = entry.IsPreview ? ViewStates.Visible : ViewStates.Gone;
            return row;
        }
    }

    sealed class ListeningListTouch(MainActivity owner) : Java.Lang.Object, View.IOnTouchListener
    {
        float startY;
        public bool OnTouch(View? view, MotionEvent? e)
        {
            if (view == null || e == null) return false;
            if (e.ActionMasked == MotionEventActions.Down)
            { startY = e.GetY(); owner.listeningBrowsePinned = true; view.Parent?.RequestDisallowInterceptTouchEvent(true); }
            else if (e.ActionMasked == MotionEventActions.Move)
                view.Parent?.RequestDisallowInterceptTouchEvent(view.CanScrollVertically(e.GetY() < startY ? 1 : -1));
            else if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
                view.Parent?.RequestDisallowInterceptTouchEvent(false);
            return false;
        }
    }
}
