using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Android.Runtime;

namespace PgrVoice.AndroidApp.Platform;

/// <summary>听书专用媒体服务；不持有屏幕捕获或游戏自动点击授权。</summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class ListeningForegroundService : Service
{
    const string Channel = "pgr_listening";
    const int NotificationId = 3102;
    const string Prefix = "pgr.listening.";
    static ListeningForegroundService? current;
    static readonly List<Action> ready = new();
    MediaSession? mediaSession;
    ListeningMediaCallback? callback;
    PowerManager.WakeLock? wakeLock;
    Handler? handler;
    Action? idleStop;
    bool destroyed;
    ListeningSession Session => AppSession.Get(this).Listening;

    public static void EnsureStarted(Context context, Action onReady)
    {
        if (current is { destroyed: false } service)
        { service.handler!.Post(onReady); return; }
        lock (ready) ready.Add(onReady);
        try { context.StartForegroundService(new Intent(context, typeof(ListeningForegroundService)).SetAction(Prefix + "START")); }
        catch { lock (ready) ready.Remove(onReady); throw; }
    }
    public static void Refresh() => current?.UpdateMedia();
    public static void StopService()
    {
        var service = current;
        // 先撤销可复用实例；快速“停止→继续”不能把新播放交给即将销毁的服务。
        current = null;
        service?.StopSelf();
    }

    public override void OnCreate()
    {
        base.OnCreate(); current = this; handler = new(Looper.MainLooper!);
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "剧情听书", NotificationImportance.Low)
        { Description = "听书播放、分支选择和锁屏控制" });
        mediaSession = new MediaSession(this, "PgrVoice.Listening");
        callback = new ListeningMediaCallback { Owner = this };
        mediaSession.SetCallback(callback, handler);
        mediaSession.Active = true;
        var power = (PowerManager)GetSystemService(PowerService)!;
        wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "PgrVoice:Listening");
        wakeLock!.SetReferenceCounted(false);
        idleStop = () => { if (!Session.IsPlaying) StopSelf(); };
    }
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        current = this;
        // Android 要求用户启动的媒体前台服务先提升，再申请音频焦点。
        StartForeground(NotificationId, BuildNotification(), ForegroundService.TypeMediaPlayback);
        string action = intent?.Action ?? "";
        Dispatch(action);
        Action[] pending;
        lock (ready) { pending = ready.ToArray(); ready.Clear(); }
        foreach (var work in pending)
            try { work(); } catch (Exception ex) { Session.Fail("无法开始听书：" + ex.Message); }
        UpdateMedia();
        return StartCommandResult.NotSticky;
    }
    internal void Dispatch(string action)
    {
        try
        {
            switch (action.Replace(Prefix, "", StringComparison.Ordinal))
            {
                case "PLAY": Session.Play(); break;
                case "PAUSE": Session.Pause(); break;
                case "NEXT": Session.Next(); break;
                case "PREVIOUS": Session.Previous(); break;
                case "STOP": Session.Stop(); StopSelf(); break;
            }
        }
        catch (Exception ex) { Session.Fail(ex.Message); }
    }
    void UpdateMedia()
    {
        if (destroyed || mediaSession == null) return;
        var session = Session;
        bool playing = session.IsPlaying;
        var displayNode = session.PreviewNode ?? (session.HasBlockingNotice ? null : session.Current);
        using var metadata = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, session.Chapter?.Title ?? "剧情听书")!
            .PutString(MediaMetadata.MetadataKeyArtist, session.PositionText)!
            .PutString(MediaMetadata.MetadataKeyDisplaySubtitle, displayNode == null && session.HasBlockingNotice ? "等待确认续接" : displayNode?.Speaker + " " + displayNode?.Text)!
            .PutLong(MediaMetadata.MetadataKeyDuration, session.IsPreviewing ? 0 : session.DurationMilliseconds)!.Build();
        mediaSession.SetMetadata(metadata);
        using var state = new PlaybackState.Builder()
            .SetActions(PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause |
                PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious | PlaybackState.ActionStop | PlaybackState.ActionSeekTo)!
            .SetState(playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused,
                session.IsPreviewing ? 0 : session.PositionMilliseconds, session.Speed, SystemClock.ElapsedRealtime())!.Build();
        mediaSession.SetPlaybackState(state);
        if (playing) { if (wakeLock?.IsHeld != true) wakeLock?.Acquire(); }
        else if (wakeLock?.IsHeld == true) wakeLock.Release();
        handler?.RemoveCallbacks(idleStop!);
        if (!playing && idleStop != null) handler?.PostDelayed(idleStop, 5 * 60_000);
        ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, BuildNotification());
    }
    PendingIntent Command(string action, int id) => PendingIntent.GetService(this, id,
        new Intent(this, typeof(ListeningForegroundService)).SetAction(Prefix + action),
        PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    Notification BuildNotification()
    {
        var session = Session;
        var intent = new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask);
        intent.PutExtra("page", "Listening");
        var open = PendingIntent.GetActivity(this, 3102, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        var style = new Notification.MediaStyle().SetMediaSession(mediaSession!.SessionToken)!.SetShowActionsInCompactView(0, 1, 2);
        var builder = new Notification.Builder(this, Channel)
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .SetContentTitle(session.Chapter?.Title ?? "剧情听书")
            .SetContentText(session.IsPreviewing ? "只试听当前一句 · 结束后停住" : session.Choices.Count > 0 ? "请打开听书页选择支线" : session.PositionText)
            .SetSubText(session.Status)
            .SetVisibility(NotificationVisibility.Public)
            .SetContentIntent(open).SetOnlyAlertOnce(true).SetOngoing(session.IsPlaying)
            .SetStyle(style);
        builder.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(this, Android.Resource.Drawable.IcMediaPrevious), "上一句", Command("PREVIOUS", 3111)).Build());
        builder.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(this, session.IsPlaying ? Android.Resource.Drawable.IcMediaPause : Android.Resource.Drawable.IcMediaPlay),
            session.IsPlaying ? "暂停" : "继续", Command(session.IsPlaying ? "PAUSE" : "PLAY", 3112)).Build());
        builder.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(this, Android.Resource.Drawable.IcMediaNext), "下一句", Command("NEXT", 3113)).Build());
        builder.AddAction(new Notification.Action.Builder(Icon.CreateWithResource(this, Android.Resource.Drawable.IcMenuCloseClearCancel), "停止", Command("STOP", 3114)).Build());
        return builder.Build();
    }
    public override IBinder? OnBind(Intent? intent) => null;
    public override void OnDestroy()
    {
        destroyed = true;
        bool owned = ReferenceEquals(current, this);
        if (owned) current = null;
        // 用户停止后可能已请求新服务；旧服务销毁不能清除它的启动回调或暂停它。
        if (owned) lock (ready) ready.Clear();
        handler?.RemoveCallbacksAndMessages(null);
        if (owned) Session.ServiceStopped();
        if (callback != null) callback.Owner = null;
        if (mediaSession != null) { mediaSession.Active = false; mediaSession.Release(); mediaSession.Dispose(); mediaSession = null; }
        if (wakeLock?.IsHeld == true) wakeLock.Release();
        wakeLock?.Dispose(); wakeLock = null;
        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
}

[Register("cn/pgrvoice/player/audio/ListeningMediaCallback")]
public sealed class ListeningMediaCallback : MediaSession.Callback
{
    internal ListeningForegroundService? Owner { get; set; }
    public ListeningMediaCallback() { }
    public override void OnPlay() => Owner?.Dispatch("PLAY");
    public override void OnPause() => Owner?.Dispatch("PAUSE");
    public override void OnSkipToNext() => Owner?.Dispatch("NEXT");
    public override void OnSkipToPrevious() => Owner?.Dispatch("PREVIOUS");
    public override void OnStop() => Owner?.Dispatch("STOP");
    public override void OnSeekTo(long pos)
    {
        if (Owner is { } owner) AppSession.Get(owner).Listening.SeekMilliseconds(pos);
    }
}
