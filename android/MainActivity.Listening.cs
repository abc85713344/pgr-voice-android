using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using PgrVoice.AndroidApp.Ui;
using PgrVoice.Listening;

namespace PgrVoice.AndroidApp;

public sealed partial class MainActivity
{
    TextView? listeningPosition, listeningSpeaker, listeningDialogue, listeningStatus, listeningResume;
    Button? listeningPlay, listeningPolicy, listeningSpeed, listeningSleep, listeningBookmark;
    SeekBar? listeningSeek;
    TextView? listeningTime;
    LinearLayout? listeningChoices, listeningBookmarks;
    string? renderedListeningPack, renderedListeningChapter;
    string renderedListeningChoices = "", renderedListeningBookmarks = "";
    Action? listeningProgressTick;
    bool listeningUiVisible, listeningTickScheduled, listeningSeeking;
    string? listeningSeekNode;
    long listeningSeekDuration;

    void ClearListeningViews()
    {
        ClearListeningDirectoryViews();
        listeningPosition = listeningSpeaker = listeningDialogue = listeningStatus = listeningResume = null;
        listeningPlay = listeningPolicy = listeningSpeed = listeningSleep = listeningBookmark = null;
        listeningSeek = null; listeningTime = null; listeningSeeking = false;
        listeningChoices = listeningBookmarks = null;
        renderedListeningPack = renderedListeningChapter = null;
        renderedListeningChoices = renderedListeningBookmarks = "";
        if (listeningProgressTick != null) pageScroll.RemoveCallbacks(listeningProgressTick);
        listeningTickScheduled = false;
    }

    void OnListeningChanged()
    {
        if (!alive) return;
        RunOnUiThread(() =>
        {
            if (!alive) return;
            UpdateStatus();
            if (page != 5) return;
            if (renderedListeningPack != session.Listening.Pack?.Id || renderedListeningChapter != session.Listening.Chapter?.Id)
                Render();
            else UpdateListeningPage();
        });
    }

    void ListeningEntrance()
    {
        var card = new LinearLayout(this) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        card.SetGravity(GravityFlags.CenterVertical); card.SetPadding(Dp(14), Dp(12), Dp(12), Dp(12));
        card.Background = PgrTheme.Surface(this, PgrTheme.Raised, PgrTheme.Border, PgrTheme.Red);
        card.ContentDescription = "听书，按大章节连续收听，记住上次位置";
        card.Click += (_, _) => Safe(() => ShowPage(5));
        card.AddView(Icon("headphones", PgrTheme.Red, 28), new LinearLayout.LayoutParams(Dp(38), Dp(38)) { RightMargin = Dp(10) });
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var heading = Text("听书 / 让故事继续", 17); heading.SetTypeface(Typeface.Default, TypefaceStyle.Bold); heading.SetPadding(0, 0, 0, Dp(4)); body.AddView(heading);
        string hint = session.Listening.Pack == null ? "整章连播 · 独立进度 · 随手书签" : session.Listening.ResumeText;
        var detail = Text(hint, 11); detail.SetTextColor(PgrTheme.Secondary); detail.SetMaxLines(2); detail.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End; detail.SetPadding(0, 0, 0, 0); body.AddView(detail);
        card.AddView(body, new LinearLayout.LayoutParams(0, -2, 1));
        card.AddView(Icon("chevron", PgrTheme.Secondary, 18), new LinearLayout.LayoutParams(Dp(22), Dp(28)));
        content.AddView(card, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(8) });
    }

    void ListeningPage()
    {
        var player = session.Listening;
        renderedListeningPack = player.Pack?.Id; renderedListeningChapter = player.Chapter?.Id;
        var navigation = Row(content);
        ButtonIcon(Button(navigation, "返回章节", () => ShowPage(0)), "previous");
        ButtonIcon(Button(navigation, "切换大章", ChooseListeningChapter), "archive");
        ListeningHero();
        if (player.Pack == null || player.Chapter == null)
        {
            var empty = Card(content);
            empty.AddView(Text("选择一章，听完一段故事", 19));
            var hint = Text("同一大章的小节会按顺序衔接。听书位置单独保存，再次打开时从上次那一句继续。", 14); hint.SetTextColor(PgrTheme.Secondary); empty.AddView(hint);
            ButtonIcon(Button(empty, "选择大章节", ChooseListeningChapter), "play", true);
            Line("使用已导入的章节配音，无需打开游戏、屏幕捕获或自动点击权限。", 13);
            return;
        }

        ListeningPlaybackControls(content);
        BuildListeningDirectory();
        var playing = Card(content, selected: true);
        listeningPosition = Text("", 11); listeningPosition.SetTextColor(PgrTheme.Cyan); playing.AddView(listeningPosition);
        listeningSpeaker = Text("", 17); listeningSpeaker.SetTypeface(Typeface.Default, TypefaceStyle.Bold); playing.AddView(listeningSpeaker);
        listeningDialogue = Text("", 17); listeningDialogue.SetLineSpacing(Dp(4), 1); playing.AddView(listeningDialogue);
        listeningStatus = Text("", 12); listeningStatus.SetTextColor(PgrTheme.Secondary); playing.AddView(listeningStatus);
        listeningSeek = new SeekBar(this) { Max = 1000 }; listeningSeek.SetMinimumHeight(Dp(42));
        var seek = listeningSeek;
        listeningSeek.ProgressTintList = global::Android.Content.Res.ColorStateList.ValueOf(PgrTheme.Red);
        listeningSeek.ThumbTintList = global::Android.Content.Res.ColorStateList.ValueOf(PgrTheme.Foreground);
        listeningSeek.ContentDescription = "当前这一句的播放进度";
        listeningSeek.StartTrackingTouch += (_, _) =>
        { if (!ReferenceEquals(listeningSeek, seek)) return; listeningSeeking = true; listeningSeekNode = player.Current?.Id; listeningSeekDuration = player.DurationMilliseconds; };
        listeningSeek.StopTrackingTouch += (_, _) => Safe(() =>
        {
            if (!ReferenceEquals(listeningSeek, seek)) return;
            listeningSeeking = false;
            // A completed clip can advance while the user drags; never seek the new sentence with the old duration.
            if (listeningSeekDuration > 0 && listeningSeekNode == player.Current?.Id)
                player.SeekMilliseconds(listeningSeekDuration * seek.Progress / 1000);
            UpdateListeningProgress();
        });
        playing.AddView(listeningSeek);
        listeningTime = Text("", 11); listeningTime.SetTextColor(PgrTheme.Secondary); listeningTime.SetPadding(0, 0, 0, Dp(2)); playing.AddView(listeningTime);
        var shortcuts = Row(playing);
        ButtonIcon(Button(shortcuts, "小节目录", ShowListeningSections), "story");
        listeningBookmark = Button(shortcuts, "记下这句", AddListeningBookmark); ButtonIcon(listeningBookmark, "bookmark");

        listeningChoices = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.AddView(listeningChoices);

        var memory = Card(content);
        var memoryTitle = Text("收听记录", 12); memoryTitle.SetTextColor(PgrTheme.Cyan); memory.AddView(memoryTitle);
        listeningResume = Text("", 13); memory.AddView(listeningResume);
        var memoryHelp = Text("自动记住小节与句子。恢复、跳转后保持暂停，点播放再继续。", 11); memoryHelp.SetTextColor(PgrTheme.Secondary); memory.AddView(memoryHelp);

        Line("收听设置", 17);
        var settings = Card(content);
        listeningPolicy = Button(settings, "", ChooseListeningPolicy); ButtonIcon(listeningPolicy, "branch");
        var policyHelp = Text("全部听：收听所有已收录分支，未接入路线的台词以“小节补充片段”标注；手动选择：遇分支暂停等你点选；默认第一个：按首个可用选项继续。", 12); policyHelp.SetTextColor(PgrTheme.Secondary); settings.AddView(policyHelp);
        var options = Row(settings);
        listeningSpeed = Button(options, "", ChooseListeningSpeed);
        listeningSleep = Button(options, "", ChooseListeningSleep);

        var bookmarkHeading = Row(content); bookmarkHeading.SetGravity(GravityFlags.CenterVertical);
        var bookmarksLabel = Text("我的书签", 17); bookmarksLabel.SetTypeface(Typeface.Default, TypefaceStyle.Bold); bookmarkHeading.AddView(bookmarksLabel, new LinearLayout.LayoutParams(0, -2, 1));
        var manage = new Button(this) { Text = "全部 / 管理", TextSize = 12 }; PgrTheme.StyleButton(manage, quiet: true); manage.Click += (_, _) => Safe(ManageListeningBookmarks); bookmarkHeading.AddView(manage, new LinearLayout.LayoutParams(-2, Dp(48)));
        listeningBookmarks = new LinearLayout(this) { Orientation = Orientation.Vertical }; content.AddView(listeningBookmarks);
        ButtonIcon(Button(content, "停止听书并保留位置", player.Stop), "close");
        Button(content, "听书使用说明", () => Info("听书使用说明", "按大章节收听，章内小节连续衔接，到大章末尾停止。\n\n小节台词目录显示完整正文，翻阅或选中台词不影响收听。点“定位到选中台词”后暂停，点播放再继续；“回到正在听”只返回当前句的显示位置。待选分支的后文可以阅读，需要打开选项后再收听。\n\n听书进度和书签独立保存，不会改变游戏中的剧情进度。重新打开、跳转小节或恢复书签后，点播放才会发声。\n\n分支可设为全部听、手动选择或默认第一个。手动选择遇到分支会暂停，回到听书页点选后继续。全部听仅依据配音包已有的路线内容，分支衔接以包内资料为准。\n\n支持后台收听、倍速与定时关闭。通知栏可控制播放；系统限制后台活动时，请在手机的应用电池设置中允许后台运行。\n\n听书无需屏幕捕获、悬浮窗或无障碍权限。游戏配音和听书共用音频输出，开始一种播放时会暂停另一种。"));
        UpdateListeningPage();
        QueueListeningUiTick();
    }

    void ListeningHero()
    {
        if (CompactLayout || session.Listening.Pack != null) { Line(session.Listening.Chapter?.Title ?? "故事，在耳边继续", 17); return; }
        var hero = new FrameLayout(this); int height = CompactLayout ? 116 : 142;
        if (archiveArt == null)
        { using var stream = Assets!.Open("ui/story-archive-v2.png"); using var options = new BitmapFactory.Options { InSampleSize = 2 }; archiveArt = BitmapFactory.DecodeStream(stream, null, options); }
        var image = new ImageView(this); image.SetImageBitmap(archiveArt); image.SetScaleType(ImageView.ScaleType.CenterCrop); hero.AddView(image, new FrameLayout.LayoutParams(-1, -1));
        var shade = new View(this) { Background = new GradientDrawable(GradientDrawable.Orientation.LeftRight, new[] { Color.Argb(215, 10, 13, 17).ToArgb(), Color.Argb(70, 10, 13, 17).ToArgb() }) }; hero.AddView(shade, new FrameLayout.LayoutParams(-1, -1));
        var body = new LinearLayout(this) { Orientation = Orientation.Vertical }; body.SetPadding(Dp(17), Dp(11), Dp(14), Dp(11));
        var caption = Text("声音档案  /  离线听书", 11); caption.SetTextColor(PgrTheme.Cyan); caption.SetPadding(0, 0, 0, Dp(7)); body.AddView(caption);
        var heading = Text(session.Listening.Chapter?.Title ?? "故事，在耳边继续", CompactLayout ? 22 : 25); heading.SetTypeface(Typeface.Default, TypefaceStyle.Bold); heading.SetMaxLines(2); heading.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End; heading.SetPadding(0, 0, 0, Dp(6)); body.AddView(heading);
        var detail = Text(session.Listening.Chapter is { } chapter ? $"{chapter.Sections.Count:00} 个小节  /  按顺序连续播放" : "选一章 · 戴上耳机 · 从上回继续", 11); detail.SetTextColor(Color.ParseColor("#C3C7CE")); detail.SetPadding(0, 0, 0, 0); body.AddView(detail);
        hero.AddView(body, new FrameLayout.LayoutParams(-1, -1)); content.AddView(hero, new LinearLayout.LayoutParams(-1, Dp(height)) { TopMargin = Dp(5), BottomMargin = Dp(3) });
    }

    void ListeningPlaybackControls(LinearLayout parent)
    {
        var controls = Row(parent);
        ButtonIcon(Button(controls, "上一句", session.Listening.Previous), "previous");
        listeningPlay = Button(controls, "播放", ToggleListeningPlayback); ButtonIcon(listeningPlay, "play", true);
        ButtonIcon(Button(controls, "下一句", session.Listening.Next), "next");
    }

    void UpdateListeningPage()
    {
        var player = session.Listening;
        if (listeningPosition == null) return;
        UpdateListeningDirectory();
        listeningPosition.Text = player.PositionText;
        listeningSpeaker!.Text = player.Current is { Kind: "line" } node ? (string.IsNullOrWhiteSpace(node.Speaker) ? "旁白" : node.Speaker) : player.Choices.Count > 0 ? "选择下一段故事" : "声音档案";
        listeningDialogue!.Text = player.Current?.Text ?? "选择一段故事，准备开始收听。";
        listeningStatus!.Text = player.Status;
        listeningResume!.Text = player.ResumeText;
        string playText = player.IsPlaying ? "暂停" : "播放";
        if (listeningPlay!.Text != playText) { listeningPlay.Text = playText; ButtonIcon(listeningPlay, player.IsPlaying ? "pause" : "play", true); }
        listeningPlay.Enabled = player.Choices.Count == 0;
        listeningBookmark!.Enabled = player.Current?.Kind == "line";
        listeningPolicy!.Text = "分支 · " + (player.Policy == ListeningBranchPolicy.All ? "全部听取" : player.Policy == ListeningBranchPolicy.Manual ? "手动选择" : "默认第一个");
        listeningSpeed!.Text = $"倍速 · {player.Speed:0.##}×";
        UpdateListeningProgress();

        string choicesKey = string.Join("\u001f", player.Choices.Select(c => c.Id + "\u001e" + c.Label));
        if (renderedListeningChoices != choicesKey)
        {
            renderedListeningChoices = choicesKey; listeningChoices!.RemoveAllViews();
            if (player.Choices.Count > 0)
            {
                var choiceCard = Card(listeningChoices, selected: true);
                var heading = Text("分支已暂停 · 选择后继续", 16); heading.SetTextColor(PgrTheme.Cyan); choiceCard.AddView(heading);
                foreach (var choice in player.Choices)
                { string id = choice.Id; ButtonIcon(Button(choiceCard, choice.Label, () => { RequestNotifications(); player.Choose(id); }), "branch"); }
            }
        }
        string bookmarksKey = string.Join("\u001f", player.Bookmarks.Select(b => b.Id + "\u001e" + b.Label + "\u001e" + b.PositionText));
        // Include an empty-state sentinel so the first render still displays a helpful message.
        bookmarksKey = player.Bookmarks.Count + ":" + bookmarksKey;
        if (renderedListeningBookmarks != bookmarksKey)
        {
            renderedListeningBookmarks = bookmarksKey; listeningBookmarks!.RemoveAllViews();
            if (player.Bookmarks.Count == 0)
            { var empty = Text("还没有手动书签。点“记下这句”，保留想再听的一刻。", 13); empty.SetTextColor(PgrTheme.Secondary); listeningBookmarks.AddView(empty); }
            foreach (var bookmark in player.Bookmarks.Take(5))
            {
                string id = bookmark.Id;
                var button = Button(listeningBookmarks, bookmark.Label + "\n" + bookmark.PositionText, () => OpenListeningBookmark(id));
                button.TextSize = 13; ButtonIcon(button, "bookmark");
            }
            if (player.Bookmarks.Count > 5)
            { var more = Text($"另有 {player.Bookmarks.Count - 5} 个书签，点“全部 / 管理”查看。", 11); more.SetTextColor(PgrTheme.Secondary); listeningBookmarks.AddView(more); }
        }
    }

    void ResumeListeningUi() { listeningUiVisible = true; QueueListeningUiTick(); }
    void PauseListeningUi()
    {
        listeningUiVisible = false;
        if (listeningProgressTick != null) pageScroll?.RemoveCallbacks(listeningProgressTick);
        listeningTickScheduled = false;
    }
    void QueueListeningUiTick()
    {
        if (!alive || !listeningUiVisible || page != 5 || listeningTime == null || listeningTickScheduled) return;
        listeningProgressTick ??= () => { listeningTickScheduled = false; if (!alive || !listeningUiVisible || page != 5) return; UpdateListeningProgress(); QueueListeningUiTick(); };
        listeningTickScheduled = true; pageScroll.PostDelayed(listeningProgressTick, 1000);
    }
    static string ListeningClock(long milliseconds)
    { long seconds = Math.Max(0, milliseconds) / 1000; return $"{seconds / 60:00}:{seconds % 60:00}"; }
    void UpdateListeningProgress()
    {
        if (listeningTime == null || listeningSeek == null) return;
        var player = session.Listening;
        long duration = player.DurationMilliseconds, position = player.PositionMilliseconds;
        if (!listeningSeeking)
        {
            listeningSeek.Enabled = duration > 0;
            listeningSeek.Progress = duration > 0 ? (int)Math.Clamp(position * 1000 / duration, 0, 1000) : 0;
            listeningTime.Text = "本句  " + ListeningClock(position) + (duration > 0 ? " / " + ListeningClock(duration) : " · 播放后可拖动进度");
        }
        if (listeningSleep != null) listeningSleep.Text = "定时 · " + (player.SleepText == "定时关闭未开启" ? "未开启" : player.SleepText);
    }

    void ToggleListeningPlayback()
    {
        if (session.Listening.IsPlaying) session.Listening.Pause();
        else { RequestNotifications(); session.Listening.Play(); }
    }

    void ChooseListeningChapter()
    {
        var packages = session.Packages.List();
        if (packages.Count == 0) { Info("先导入配音包", "请返回章节页，导入章节 ZIP 后再收听。听书和游戏配音共用同一份音频。"); return; }
        Choose("选择大章节配音包", packages.Select(p => p.Title).ToArray(), i =>
        {
            var pack = session.Packages.Load(packages[i].PackId);
            if (pack.Chapters.Count == 0) { Info("没有章节", "这个配音包中没有可收听的大章节。"); return; }
            void OpenChapter(int index) { session.Listening.Open(pack.Id, pack.Chapters[index].Id); ShowPage(5); }
            if (pack.Chapters.Count == 1) OpenChapter(0);
            else Choose("选择大章节", pack.Chapters.Select(c => c.Title).ToArray(), OpenChapter);
        });
    }

    void ShowListeningSections()
    {
        var player = session.Listening;
        if (player.Chapter == null) return;
        var owner = player.Pack; var chapter = player.Chapter;
        var sections = player.Chapter.Sections;
        Choose("浏览小节 · 不改变收听位置", sections.Select((s, i) => $"{i + 1:00}  {s.Title}" + (player.Current?.SectionId == s.Id ? "  · 正在听" : "")).ToArray(), i =>
        {
            if (!ReferenceEquals(owner, player.Pack) || !ReferenceEquals(chapter, player.Chapter)) return;
            listeningBrowsePinned = true; listeningBrowseSection = sections[i].Id; listeningSelectedKey = null;
            UpdateListeningDirectory(true); ScrollToListeningDirectory();
        });
    }

    void ChooseListeningPolicy() => Choose("遇到支线怎么听", new[] { "全部听取：依次收听可用分支", "手动选择：遇分支暂停，点选后继续", "默认第一个：按首个可用选项继续" }, i =>
        session.Listening.SetPolicy(i == 0 ? ListeningBranchPolicy.All : i == 1 ? ListeningBranchPolicy.Manual : ListeningBranchPolicy.First));

    void ChooseListeningSpeed()
    {
        float[] speeds = { .75f, 1f, 1.25f, 1.5f, 1.75f, 2f };
        Choose("收听倍速", speeds.Select(s => $"{s:0.##}×" + (s == 1 ? " · 原速" : "")).ToArray(), i => session.Listening.SetSpeed(speeds[i]));
    }

    void ChooseListeningSleep()
    {
        int[] minutes = { 0, 15, 30, 45, 60, 90 };
        Choose("定时关闭", minutes.Select(m => m == 0 ? "关闭定时" : $"{m} 分钟后暂停").ToArray(), i => session.Listening.SetSleepMinutes(minutes[i]));
    }

    void AddListeningBookmark()
    {
        if (session.Listening.Current?.Kind != "line") return;
        // Pin the displayed sentence while the name dialog is open; natural completion
        // must not silently attach the bookmark to a later line.
        session.Listening.Pause();
        var node = session.Listening.Current;
        string excerpt = node.Text.Length > 24 ? node.Text[..24] + "…" : node.Text;
        Prompt("记下这一句 · 已暂停", (string.IsNullOrWhiteSpace(node.Speaker) ? "" : node.Speaker + " · ") + excerpt, session.Listening.AddBookmark);
    }

    void ManageListeningBookmarks()
    {
        var bookmarks = session.Listening.Bookmarks.ToArray();
        Choose("本章听书书签", bookmarks.Select(b => b.Label + "\n" + b.PositionText).ToArray(), i => OpenListeningBookmark(bookmarks[i].Id));
    }

    void OpenListeningBookmark(string id)
    {
        var bookmark = session.Listening.Bookmarks.FirstOrDefault(b => b.Id == id);
        if (bookmark == null) return;
        Choose(bookmark.Label, new[] { "恢复到这里（暂停，点播放再继续）", "删除这个书签" }, i =>
        {
            if (i == 0) session.Listening.RestoreBookmark(id);
            else Confirm("删除听书书签", bookmark.Label + "\n" + bookmark.PositionText, () => session.Listening.RemoveBookmark(id));
        });
    }
}
