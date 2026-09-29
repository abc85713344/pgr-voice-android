using Android.Content;
using Android.Media;
using Android.OS;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using PgrVoice.AndroidApp.Contracts;
using NativeAudioAttributes = Android.Media.AudioAttributes;
using MediaAudioAttributes = AndroidX.Media3.Common.AudioAttributes;

namespace PgrVoice.AndroidApp.Platform;

/// <summary>All ExoPlayer operations are marshalled to Android's main looper.</summary>
public sealed class AndroidAudioPlayer : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener, IAudioOutput
{
    private readonly Context context;
    private readonly Handler main = new(Looper.MainLooper!);
    private readonly AudioManager audioManager;
    private readonly NoisyReceiver noisy;
    private IExoPlayer? player;
    private AudioFocusRequestClass? focusRequest;
    private AudioPlaybackListener? playbackListener;
    private long requestVersion, activePlaybackId;
    private volatile bool disposed;
    private bool completionDelivered, playRequested;
    private float volume = .8f;
    private AudioStrategy strategy;
    private readonly Action checkAudioMode;

    public event Action? Completed;
    public event Action<long>? PlaybackCompleted;
    public event Action<string>? Error;
    public event Action<string>? Interrupted;
    public bool Playing => player?.IsPlaying == true;
    public long PositionMilliseconds => Math.Max(0, player?.CurrentPosition ?? 0);
    public long DurationMilliseconds => Math.Max(0, player?.Duration ?? 0);
    private float playbackSpeed = 1;
    public void SetSpeed(float speed) => OnMain(() =>
    {
        playbackSpeed = float.IsFinite(speed) ? Math.Clamp(speed, .75f, 2f) : 1;
        player?.SetPlaybackSpeed(playbackSpeed);
    });
    public AudioStrategy Strategy
    {
        get => strategy;
        set => OnMain(() =>
        {
            if (strategy == value) return;
            strategy = value;
            if (playRequested) Interrupt("播放策略已更改，请重播当前句。");
            AbandonFocus();
        });
    }

    public AndroidAudioPlayer(Context context)
    {
        this.context = context.ApplicationContext!;
        audioManager = (AudioManager)this.context.GetSystemService(Context.AudioService)!;
        noisy = new NoisyReceiver(this);
        var filter = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
        if (OperatingSystem.IsAndroidVersionAtLeast(33)) this.context.RegisterReceiver(noisy, filter, ReceiverFlags.NotExported);
        else this.context.RegisterReceiver(noisy, filter);
        checkAudioMode = CheckAudioMode;
    }

    private void EnsurePlayer()
    {
        if (player != null) return;
        player = new ExoPlayerBuilder(context).Build() ?? throw new InvalidOperationException("无法初始化音频播放器。");
        var attributes = new MediaAudioAttributes.Builder()
            .SetUsage(1)! // USAGE_MEDIA
            .SetContentType(1)! // CONTENT_TYPE_SPEECH
            .Build();
        // Focus is deliberately handled here: automatic ExoPlayer focus would interrupt the game.
        player.SetAudioAttributes(attributes, false);
        player.SetHandleAudioBecomingNoisy(false);
        player.Volume = volume;
    }

    public void Play(string file) => PlayTagged(file);

    public long PlayTagged(string file) => PlayTagged(file, 0);

    public long PlayTagged(string file, long positionMilliseconds)
    {
        // Invalidate on the calling thread, before a pending main-looper callback can run.
        long ticket = Interlocked.Increment(ref requestVersion);
        OnMain(() =>
        {
            if (disposed || ticket != Volatile.Read(ref requestVersion)) return;
            try
            {
                StopCore();
                if (!File.Exists(file)) throw new FileNotFoundException("这一句音频文件缺失", file);
                if (audioManager.Mode != Mode.Normal) { Interrupted?.Invoke("通话或其他语音会话正在使用音频，请结束后重播。"); return; }
                EnsurePlayer();
                if (strategy != AudioStrategy.Simultaneous && !RequestFocus())
                { Interrupted?.Invoke("暂时无法取得音频焦点，请稍后重播。"); return; }
                completionDelivered = false;
                playRequested = true;
                activePlaybackId = ticket;
                string mediaId = "pgr-voice-" + ticket.ToString(System.Globalization.CultureInfo.InvariantCulture);
                using var localFile = new Java.IO.File(file);
                using var item = new MediaItem.Builder().SetMediaId(mediaId)!.SetUri(Android.Net.Uri.FromFile(localFile)!)!.Build();
                player!.SetMediaItem(item);
                player.SetPlaybackSpeed(playbackSpeed);
                playbackListener = new AudioPlaybackListener(this, ticket, mediaId, Math.Max(0, positionMilliseconds));
                player.AddListener(playbackListener);
                ScheduleAudioModeCheck();
                player.Prepare();
                // Prepare may deliver an error synchronously and clear this request.
                if (activePlaybackId == ticket && ticket == Volatile.Read(ref requestVersion) && playbackListener?.StartPositionMs == 0) player.Play();
            }
            catch (Exception ex) { StopCore(); Error?.Invoke("无法播放配音：" + ex.Message); }
        });
        return ticket;
    }

    public void Stop()
    {
        long ticket = Interlocked.Increment(ref requestVersion);
        OnMain(() => { if (ticket == Volatile.Read(ref requestVersion)) StopCore(); });
    }
    public void Pause()
    {
        long ticket = Interlocked.Increment(ref requestVersion);
        OnMain(() =>
        {
            if (ticket != Volatile.Read(ref requestVersion)) return;
            playRequested = false; completionDelivered = true; activePlaybackId = 0;
            main.RemoveCallbacks(checkAudioMode); DetachPlaybackListener(); player?.Pause(); AbandonFocus();
        });
    }
    public void SetVolume(float value) => OnMain(() => { volume = Math.Clamp(value, 0, 1); if (player != null) player.Volume = volume; });

    private bool RequestFocus()
    {
        using var attributes = new NativeAudioAttributes.Builder()!
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Speech)!.Build();
        focusRequest = new AudioFocusRequestClass.Builder(strategy == AudioStrategy.Listening ? AudioFocus.Gain : AudioFocus.GainTransientMayDuck)
            .SetAudioAttributes(attributes!)!
            .SetAcceptsDelayedFocusGain(false)!
            .SetOnAudioFocusChangeListener(this, main)!
            .Build();
        bool granted = audioManager.RequestAudioFocus(focusRequest!) == AudioFocusRequest.Granted;
        if (!granted) AbandonFocus();
        return granted;
    }
    private void AbandonFocus()
    {
        var focus = focusRequest; focusRequest = null;
        if (focus != null) { try { audioManager.AbandonAudioFocusRequest(focus); } finally { focus.Dispose(); } }
    }
    private void StopCore()
    {
        playRequested = false;
        activePlaybackId = 0;
        main.RemoveCallbacks(checkAudioMode);
        completionDelivered = true;
        DetachPlaybackListener();
        player?.Stop();
        player?.ClearMediaItems();
        AbandonFocus();
    }
    private void Interrupt(string reason)
    {
        if (!playRequested) return;
        Interlocked.Increment(ref requestVersion);
        playRequested = false;
        completionDelivered = true; activePlaybackId = 0;
        main.RemoveCallbacks(checkAudioMode);
        DetachPlaybackListener();
        player?.Pause();
        AbandonFocus();
        Interrupted?.Invoke(reason);
    }
    private void CheckAudioMode()
    {
        if (disposed || !playRequested) return;
        // Does not request phone-state access. Ringing/calls/VoIP are detected through the audio mode.
        if (playRequested && audioManager.Mode != Mode.Normal) Interrupt("检测到通话或语音会话，配音已暂停。结束后请重播当前句。");
        if (playRequested) ScheduleAudioModeCheck();
    }
    private void ScheduleAudioModeCheck()
    {
        main.RemoveCallbacks(checkAudioMode);
        if (!disposed && playRequested) main.PostDelayed(checkAudioMode, 500);
    }
    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient or AudioFocus.LossTransientCanDuck)
            OnMain(() => Interrupt("其他应用正在使用音频，配音已暂停，请确认后重播。"));
    }
    private bool IsCurrent(AudioPlaybackListener listener) => !disposed
        && ReferenceEquals(playbackListener, listener)
        && listener.Ticket == activePlaybackId && activePlaybackId == Volatile.Read(ref requestVersion)
        && playRequested && !completionDelivered;

    internal void OnPlaybackEvents(AudioPlaybackListener listener)
    {
        PrepareResumePosition(listener);
        // onEvents observes a coherent group of state/media changes. A callback from an old
        // listener must never use the new media item's global playRequested flag as identity.
        if (!IsCurrent(listener) || !listener.HasPlayed || player == null
            || player.PlaybackState != 4 || !player.PlayWhenReady || player.PlayerError != null // STATE_ENDED
            || player.Duration <= 0 || player.CurrentMediaItem?.MediaId != listener.MediaId) return;
        long completedTicket = listener.Ticket;
        completionDelivered = true; playRequested = false; activePlaybackId = 0;
        main.RemoveCallbacks(checkAudioMode); AbandonFocus();
        PlaybackCompleted?.Invoke(completedTicket);
        if (!disposed && completedTicket == Volatile.Read(ref requestVersion)) Completed?.Invoke();
    }
    internal void PrepareResumePosition(AudioPlaybackListener listener)
    {
        if (!IsCurrent(listener) || player == null || player.PlaybackState != 3 || listener.StartPositionMs <= 0) return;
        long requested = listener.StartPositionMs;
        listener.StartPositionMs = 0;
        // 音频更新可能缩短当前句，超出新长度时从本句开头续听，不能卡在从未播放的 ENDED。
        long duration = player.Duration;
        player.SeekTo(duration > 0 && requested < duration - 150 ? requested : 0);
        if (IsCurrent(listener)) player.Play();
    }
    internal void OnPlaybackPlayingChanged(AudioPlaybackListener listener, bool playing)
    {
        if (playing && IsCurrent(listener)) listener.HasPlayed = true;
    }
    internal void OnPlaybackError(AudioPlaybackListener listener, PlaybackException? error)
    {
        if (!IsCurrent(listener)) return;
        StopCore(); Error?.Invoke("配音播放失败：" + (error?.Message ?? "未知音频错误"));
    }
    private void DetachPlaybackListener()
    {
        var old = playbackListener; playbackListener = null;
        if (old == null) return;
        // RemoveListener can synchronously flush onEvents. Identity is already invalid above.
        // Retain the Java peer until the current JNI/main-looper stack has returned.
        player?.RemoveListener(old);
        main.Post(old.Dispose);
    }
    private void OnMain(Action action)
    {
        if (Looper.MyLooper() == Looper.MainLooper) action();
        else main.Post(action);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            Interlocked.Increment(ref requestVersion);
            OnMain(() =>
            {
                main.RemoveCallbacks(checkAudioMode);
                StopCore();
                try { context.UnregisterReceiver(noisy); } catch { }
                player?.Release(); player?.Dispose(); player = null;
                noisy.Dispose();
            });
        }
        base.Dispose(disposing);
    }
    private sealed class NoisyReceiver(AndroidAudioPlayer owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy)
                owner.Interrupt("耳机或蓝牙音频已断开，配音已暂停。确认输出设备后请重播。");
        }
    }
}

/// <summary>A Java peer bound to exactly one playback request, never reused for another file.</summary>
[Register("cn/pgrvoice/player/audio/PlaybackListener")]
public sealed class AudioPlaybackListener : Java.Lang.Object, IPlayerListener
{
    private readonly AndroidAudioPlayer? owner;
    internal long Ticket { get; }
    internal string MediaId { get; } = "";
    internal bool HasPlayed { get; set; }
    internal long StartPositionMs { get; set; }

    public AudioPlaybackListener() { }
    internal AudioPlaybackListener(AndroidAudioPlayer owner, long ticket, string mediaId, long startPositionMs = 0)
    { this.owner = owner; Ticket = ticket; MediaId = mediaId; StartPositionMs = startPositionMs; }

    public void OnEvents(IPlayer? player, PlayerEvents? events) => owner?.OnPlaybackEvents(this);
    public void OnIsPlayingChanged(bool isPlaying) => owner?.OnPlaybackPlayingChanged(this, isPlaying);
    public void OnPlaybackStateChanged(int playbackState) => owner?.PrepareResumePosition(this);
    public void OnPlayerError(PlaybackException? error) => owner?.OnPlaybackError(this, error);
}
