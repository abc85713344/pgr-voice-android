using Android.Content;
using Android.OS;
using PgrVoice.AndroidApp.Contracts;
using PgrVoice.AndroidApp.Platform;
using PgrVoice.Listening;
using CoreSession = PgrVoice.Listening.ListeningSession;
using Path = System.IO.Path;

namespace PgrVoice.AndroidApp;

public sealed record ListeningBookmarkView(string Id, string Label, string PositionText);
public sealed record ListeningChoiceView(string Id, string Label);
public sealed class ListeningPreferences
{
    public string? LastPackId { get; set; }
    public string? LastChapterId { get; set; }
    public float Speed { get; set; } = 1;
}

/// <summary>由主线程调用的独立听书会话。所有回调绑定代次，永不推进游戏存档。</summary>
public sealed class ListeningSession
{
    readonly Context context;
    readonly IChapterStorage packages;
    readonly Action beforePlay;
    readonly SessionDiagnostics diagnostics;
    readonly Handler main = new(Looper.MainLooper!);
    readonly ListeningProgressStore store;
    readonly string settingsPath;
    readonly ListeningPreferences preferences;
    readonly Action timerTick;
    AndroidAudioPlayer? audio;
    CoreSession? core;
    ListeningProgressDocument? document;
    long epoch, ticket, offset, sleepDeadline, startedAt, lastSavedAt;
    long knownDuration;
    string? durationNodeId;
    bool running, timerQueued, resumeNeedsSelection;
    int skipped, notices;
    string? resumeNotice;
    string status = "选择大章节，开始离线听书。";
    string? saveWarning;
    public event Action? Changed;
    public Pack? Pack => core?.Pack;
    public Chapter? Chapter => core?.Chapter;
    public Node? Current => core?.Current?.Node;
    public bool IsPlaying => running;
    public string Status { get => status + (saveWarning == null ? "" : "；" + saveWarning); private set => status = value; }
    public ListeningBranchPolicy Policy => core?.Policy ?? ListeningBranchPolicy.First;
    public IReadOnlySet<string> AvailableNodeIds => core?.Items.Where(x => x.Kind == ListeningItemKind.Line)
        .Select(x => x.NodeId).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
    public float Speed => preferences.Speed;
    public long PositionMilliseconds => ticket != 0 && audio != null ? audio.PositionMilliseconds : offset;
    public long DurationMilliseconds
    {
        get
        {
            if (durationNodeId != Current?.Id) { durationNodeId = Current?.Id; knownDuration = 0; }
            if (ticket != 0 && audio?.DurationMilliseconds > 0) knownDuration = audio.DurationMilliseconds;
            return knownDuration;
        }
    }
    public string PositionText => core?.Current is { } item
        ? $"{Chapter!.Title} · {item.PositionLabel}" + (item.BranchLabel.Length > 0 ? $" · {item.BranchLabel}" : "")
        : core?.Completed == true ? $"{Chapter!.Title} · 已听完" : "尚未选择大章节";
    public string ResumeText
    {
        get
        {
            if (resumeNeedsSelection) return "上回听书记录与新版章节不匹配，原记录已保留。请选小节，或点击播放从开头重听。";
            var resume = ChapterProgress?.Resume;
            return resume == null ? "还没有收听记录，从本章开头开始。" :
                resume.Completed ? "上回已听完本章，可从目录选择小节重听。" :
                $"上回听到：{resume.SectionTitle} · 第 {resume.LineNumber} 句" +
                (resume.PositionMs > 0 ? $" · {resume.PositionMs / 1000} 秒" : "") +
                (resume.Text.Length > 0 ? $"\n{resume.Speaker}：{resume.Text}" : "");
        }
    }
    public string SleepText => sleepDeadline == 0 ? "定时关闭未开启" :
        $"约 {Math.Max(1, (sleepDeadline - SystemClock.ElapsedRealtime() + 59_999) / 60_000)} 分钟后暂停";
    ListeningChapterProgress? ChapterProgress => document != null && Chapter != null ? document.ForChapter(Chapter.Id) : null;
    public IReadOnlyList<ListeningBookmarkView> Bookmarks => ChapterProgress?.Bookmarks.Select(x =>
        new ListeningBookmarkView(x.Id, x.Label, $"{x.Snapshot.SectionTitle} · 第 {x.Snapshot.LineNumber} 句")).ToArray() ?? Array.Empty<ListeningBookmarkView>();
    public IReadOnlyList<ListeningChoiceView> Choices => core?.Current?.Kind == ListeningItemKind.Choice
        ? core.Current.Options?.Select(x => new ListeningChoiceView(x.Id, x.Label)).ToArray() ?? Array.Empty<ListeningChoiceView>()
        : Array.Empty<ListeningChoiceView>();

    public ListeningSession(Context context, IChapterStorage packages, Action beforePlay, SessionDiagnostics diagnostics)
    {
        this.context = context; this.packages = packages; this.beforePlay = beforePlay; this.diagnostics = diagnostics;
        string root = Path.Combine(context.FilesDir!.AbsolutePath, "listening");
        store = new(root); settingsPath = Path.Combine(root, "preferences.json");
        try { preferences = Json.ReadWithBackup<ListeningPreferences>(settingsPath, out _); } catch { preferences = new(); }
        preferences.Speed = float.IsFinite(preferences.Speed) ? Math.Clamp(preferences.Speed, .75f, 2f) : 1;
        timerTick = Tick;
    }
    public void OpenLast()
    {
        if (core != null || preferences.LastPackId == null || preferences.LastChapterId == null) return;
        try { Open(preferences.LastPackId, preferences.LastChapterId); }
        catch (Exception ex) { Fail("上次章节暂不可用，请重新选择大章。" + ex.Message); }
    }
    public void Open(string packId, string chapterId)
    {
        Pause();
        // 先完整加载，失败时仍保留此前可用章节。
        var pack = packages.Load(packId);
        var next = new CoreSession(pack, chapterId);
        var nextDocument = store.Load(packId, out var readNotice);
        var saved = nextDocument.ForChapter(chapterId).Resume;
        long nextOffset = 0;
        resumeNotice = null; resumeNeedsSelection = false;
        if (saved != null)
        {
            if (next.TryRestore(saved, out var reason)) nextOffset = Math.Max(0, next.ResumePositionMs);
            else { resumeNeedsSelection = true; resumeNotice = "剧情已更新，上次位置无法直接恢复，请从目录重新选择。"; }
        }
        core = next; document = nextDocument; offset = nextOffset; skipped = notices = 0;
        preferences.LastPackId = packId; preferences.LastChapterId = chapterId; SavePreferences();
        Status = resumeNotice ?? (string.IsNullOrEmpty(readNotice) ? "已载入听书位置，点击继续收听后才发声。" : readNotice);
        Notify();
    }
    public void PackageUpdated(string packId)
    {
        if (Pack?.Id != packId || Chapter == null) return;
        string chapterId = Chapter.Id;
        try { Open(packId, chapterId); }
        catch (Exception ex)
        {
            core = null; document = null; offset = 0;
            Status = "章节内容已更新，请重新选择听书大章。"; diagnostics.Log("听书章节更新", ex.Message); Notify();
        }
    }
    AndroidAudioPlayer Audio
    {
        get
        {
            if (audio != null) return audio;
            audio = new(context) { Strategy = AudioStrategy.Listening };
            audio.SetVolume(1); audio.SetSpeed(Speed);
            audio.PlaybackCompleted += OnCompleted;
            audio.Error += Fail;
            audio.Interrupted += Fail;
            return audio;
        }
    }
    public void Play()
    {
        if (core == null) { Status = "请先选择大章节。"; Notify(); return; }
        if (running) return;
        if (core.Completed) { Status = "本章已听完，请从目录选择小节重听。"; Notify(); return; }
        if (Choices.Count > 0) { Status = "请选择要听的支线，选择后继续播放。"; Notify(); return; }
        beforePlay();
        resumeNeedsSelection = false;
        running = true; long request = ++epoch; startedAt = SystemClock.ElapsedRealtime();
        Status = "正在准备听书…"; Notify(); QueueTick();
        try { ListeningForegroundService.EnsureStarted(context, () => { if (running && epoch == request) PlayCurrent(request); }); }
        catch (Exception ex) { Fail("无法开始后台播放：" + ex.Message); }
    }
    void PlayCurrent(long request)
    {
        if (!running || core == null || request != epoch) return;
        if (sleepDeadline > 0 && SystemClock.ElapsedRealtime() >= sleepDeadline) { SleepExpired(); return; }
        // 分批跨过无音频节点，不递归、不堵塞主线程；每批检查取消代次。
        for (int i = 0; i < 32; i++)
        {
            if (core.Completed || core.Current == null)
            { PauseInternal(false); offset = 0; SaveResume(); Status = "本章已听完。" + SkippedText; Notify(); return; }
            var item = core.Current;
            if (item.Kind == ListeningItemKind.Choice)
            { PauseInternal(false); offset = 0; SaveResume(); Status = "遇到支线，请手动选择后继续收听。" + SkippedText; Notify(); return; }
            if (item.Kind == ListeningItemKind.Notice)
            { notices++; diagnostics.Log("听书提示", item.Notice); core.MoveNext(); offset = 0; continue; }
            string? file = item.Node == null ? null : core.Pack.ResolveAudio(item.Node);
            if (file == null || !File.Exists(file))
            { skipped++; core.MoveNext(); offset = 0; continue; }
            Status = "正在收听" + (item.BranchLabel.Length > 0 ? " · " + item.BranchLabel : "") + SkippedText;
            SaveResume(); startedAt = SystemClock.ElapsedRealtime();
            long audioTicket = Audio.PlayTagged(file, offset);
            // PlayTagged 可同步报告音频焦点拒绝/文件错误；失败后不能重新绑定旧回调。
            if (request != epoch || !running) return;
            ticket = audioTicket;
            Notify(); QueueTick(); return;
        }
        main.Post(() => PlayCurrent(request));
    }
    string SkippedText => (skipped > 0 ? $"；本次已跳过 {skipped} 句未收录配音" : "") +
        (notices > 0 ? $"；本章有 {notices} 处路线资料提示，补充片段单独标注" : "");
    void OnCompleted(long completedTicket)
    {
        if (!running || completedTicket != ticket || core == null) return;
        ticket = 0; offset = 0;
        core.MoveNext(); SaveResume();
        long request = epoch;
        main.Post(() => PlayCurrent(request));
    }
    public void Pause() { PauseInternal(true); Status = "听书已暂停，位置已保存。" + SkippedText; Notify(); }
    void PauseInternal(bool captureOffset)
    {
        _ = DurationMilliseconds;
        if (captureOffset && ticket != 0 && audio != null) offset = audio.PositionMilliseconds;
        epoch++; running = false; ticket = 0; audio?.Stop(); SaveResume();
        main.RemoveCallbacks(timerTick); timerQueued = false;
        if (sleepDeadline != 0) QueueTick();
    }
    public void Stop()
    {
        sleepDeadline = 0; PauseInternal(true);
        audio?.Dispose(); audio = null;
        Status = "听书已停止，续听位置和书签已保存。"; Notify(); ListeningForegroundService.StopService();
    }
    public void ServiceStopped()
    {
        sleepDeadline = 0; PauseInternal(true); audio?.Dispose(); audio = null;
        Status = "后台听书已停止，可点击继续收听。"; Notify();
    }
    public void Fail(string message)
    { PauseInternal(true); Status = message + "；已保留听书位置。"; diagnostics.Log("听书暂停", message); Notify(); }
    public void Next() => Move(() => core!.MoveNext());
    public void Previous() => Move(() => core!.Previous());
    void Move(Func<bool> move)
    {
        if (core == null) return;
        bool wasPlaying = running; PauseInternal(true);
        var previous = core.Current;
        bool moved = move() || !ReferenceEquals(previous, core.Current);
        if (moved) { offset = 0; resumeNeedsSelection = false; SaveResume(); Status = "已定位，点击继续收听。"; }
        else Status = Choices.Count > 0 ? "请先选择支线，或从目录跳到其他小节。" : "当前位置无法按此方式跳转，请选择目录中的小节。";
        Notify(); if (wasPlaying && moved) Play();
    }
    public void JumpToSection(string id)
    { if (core != null) { Pause(); Move(() => core.SeekSection(id)); } }
    public void JumpToNode(string id)
    { if (core != null) { Pause(); Move(() => core.SeekNode(id)); } }
    public void Choose(string optionId)
    {
        if (core == null) return;
        PauseInternal(true);
        if (!core.Choose(optionId)) { Status = "支线选择已变化，请重新打开选择列表。"; Notify(); return; }
        offset = 0; SaveResume(); Notify(); Play();
    }
    public void SetPolicy(ListeningBranchPolicy policy)
    {
        if (core == null || Policy == policy) return;
        PauseInternal(true); core.SetPolicy(policy); offset = 0; SaveResume();
        Status = "支线方式已更改，已暂停；核对当前位置后继续收听。"; Notify();
    }
    public void AddBookmark(string label)
    {
        if (core?.Current == null || ChapterProgress == null) return;
        ChapterProgress.Bookmarks.Add(new ListeningBookmark
        { Label = string.IsNullOrWhiteSpace(label) ? core.Current.PositionLabel : label.Trim(), Snapshot = core.Capture(PositionMilliseconds) });
        SaveDocument(); Status = "已添加听书书签。"; Notify();
    }
    public void RemoveBookmark(string id)
    { ChapterProgress?.Bookmarks.RemoveAll(x => x.Id == id); SaveDocument(); Notify(); }
    public void RestoreBookmark(string id)
    {
        var mark = ChapterProgress?.Bookmarks.FirstOrDefault(x => x.Id == id);
        if (core == null || mark == null) return;
        PauseInternal(true);
        if (core.TryRestore(mark.Snapshot, out var reason))
        { offset = Math.Max(0, core.ResumePositionMs); SaveResume(); Status = "已回到书签，点击继续收听。"; }
        else Status = "此书签对应的剧情已变化，请从目录重新选择。";
        Notify();
    }
    public void SetSpeed(float speed)
    { preferences.Speed = float.IsFinite(speed) ? Math.Clamp(speed, .75f, 2f) : 1; audio?.SetSpeed(Speed); SavePreferences(); Notify(); }
    public void SetSleepMinutes(int minutes)
    { sleepDeadline = minutes <= 0 ? 0 : SystemClock.ElapsedRealtime() + Math.Clamp(minutes, 1, 180) * 60_000L; QueueTick(); Notify(); }
    public void SeekMilliseconds(long position)
    {
        if (core == null || core.Current?.Kind != ListeningItemKind.Line) return;
        bool wasPlaying = running;
        long duration = DurationMilliseconds;
        PauseInternal(false); offset = Math.Clamp(position, 0, duration > 0 ? Math.Max(0, duration - 100) : Math.Max(0, position));
        SaveResume(); Notify(); if (wasPlaying) Play();
    }
    void QueueTick()
    { if (!timerQueued && (running || sleepDeadline != 0)) { timerQueued = true; main.PostDelayed(timerTick, 1000); } }
    void Tick()
    {
        timerQueued = false;
        long now = SystemClock.ElapsedRealtime();
        if (sleepDeadline > 0 && now >= sleepDeadline) { SleepExpired(); return; }
        if (running && now - startedAt > 20_000 && ticket == 0) { Fail("后台播放启动等待超时，请重试"); return; }
        if (running && now - startedAt > 30_000 && audio?.Playing != true)
        { Fail("此句音频无法准备完成，请点下一句或更换配音包"); return; }
        if (running && now - lastSavedAt >= 5000) { SaveResume(); ListeningForegroundService.Refresh(); }
        QueueTick();
    }
    void SleepExpired()
    { sleepDeadline = 0; PauseInternal(true); Status = "睡眠定时已到，已暂停并保存当前位置。"; Notify(); }
    public void SaveResume()
    {
        if (core == null || ChapterProgress == null || resumeNeedsSelection) return;
        ChapterProgress.Resume = core.Capture(PositionMilliseconds); lastSavedAt = SystemClock.ElapsedRealtime(); SaveDocument();
    }
    void SaveDocument()
    {
        if (document == null) return;
        try { store.Save(document); saveWarning = null; }
        catch (Exception ex) { diagnostics.Log("听书存档失败", ex.Message); saveWarning = "听书进度尚未写入，请检查手机可用存储空间"; }
    }
    void SavePreferences()
    { try { Json.Save(settingsPath, preferences); } catch (Exception ex) { diagnostics.Log("听书设置", ex.Message); } }
    void Notify() { Changed?.Invoke(); ListeningForegroundService.Refresh(); }
}
