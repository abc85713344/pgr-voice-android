# 安卓系统接入

这部分实现 Android 10（API 29）及以上的原生服务、屏幕捕获、悬浮控制和音频；编译目标为 API 36。无需 root，普通播放、OCR 定位和自动跟随不需要无障碍服务。可选的共同线自动播放通过独立无障碍服务发送单次游戏触碰，须用户主动在系统设置中授权，并在游戏前台明确开启；未授权时普通模式仍可使用。

## 集成约定

会话层依赖 `Contracts.IScreenInput`、`Contracts.IAudioOutput`、`Contracts.IChapterStorage` 与 `Ocr.IOcrEngine`。具体实现分别是 `AndroidScreenInput`、`AndroidAudioPlayer`、`AndroidChapterStorage` 和 OCR 引擎；屏幕输入适配器统一订阅系统服务，不能再让会话层重复订阅服务的静态帧事件。`CapturedFrame`、`AudioStrategy` 属于 `Contracts` 命名空间。

- `VoiceForegroundService.EnsureStarted(context, ready)` 在常驻通知实际建立后回调。需要音频焦点的首次播放应在此回调后执行；调用者须在回调时再次检查请求是否已取消或切章。
- `VoiceForegroundService.UpdateNotification(message, page)` 更新常驻通知及点击后的页面入口，不会启动服务。相同文本和页面跳过，持续更新合并为最多每秒一次。手动 OCR 结果或截图已准备时，上层应通知用户点击返回；Android 阻止后台页面弹出时仍有用户主动打开的路径。通知使用不可变、可更新的 PendingIntent。
- Activity 使用系统 `MediaProjectionManager` 请求屏幕授权，然后调用 `StartCapture(context, Result.Ok, data)`。授权结果只传一次，不保存、不重用。`CaptureActive` 和 `CaptureSessionId` 描述当前会话。
- `FrameAvailable` 仅允许应用会话层订阅一次。事件在捕获线程发出，约 200 ms 一帧；消费者须快速转交或丢弃，且无论采用还是丢弃，都必须 `Dispose` 收到的 `CapturedFrame`。后续异步处理需先复制所需像素。帧带单调时钟时间戳和会话号，不能跨会话使用旧帧。
- 捕获保留最新帧，逐帧明确 `Image.Close()` 归还系统缓冲槽；截图不落盘。ROI、OCR 调度、UI 可见时停识别及自动推进规则属于上层。
- 每份授权只创建一个 VirtualDisplay。旋转或折叠后的尺寸变化通过调整显示和替换 Surface 处理；Android 14 起采用捕获内容尺寸回调，以支持单应用共享。Android 10～13 使用配置变化。
- 锁屏、系统取消共享或捕获错误会释放捕获资源并触发 `CaptureStopped`；不会自行重开共享。上层解除自动跟随并保留手动操作。用户点击通知“停止”触发 `StopRequested`；`StopAll` 幂等，可由上层停止动作再次调用。
- `OverlayController` 只接收自己窗口里的拖动和按钮点击。面板之外不拦截点击；分支、历史、章节和定位由回调交给上层。拒绝悬浮窗授权时通过 `Error` 反馈，主界面仍可手动操作。
- `AndroidAudioPlayer` 所有 ExoPlayer 调用归入主线程。默认 `Simultaneous` 不请求焦点；`VoicePriority` 使用短时 may-duck 焦点。完成事件只表示当前音频播完，绝不自行推进剧情。耳机/蓝牙断开、丢失焦点、检测到非普通音频模式时暂停并提示，恢复后不擅自重播。

## 权限

主 Manifest 声明 `FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_MEDIA_PLAYBACK`、`FOREGROUND_SERVICE_MEDIA_PROJECTION`、`SYSTEM_ALERT_WINDOW` 与 `POST_NOTIFICATIONS`。Android 13 起通知权限由 Activity 引导；屏幕捕获和悬浮窗均通过系统授权。服务声明 `exported=false`、`mediaPlayback|mediaProjection`，只在用户可见界面或用户操作后启动，返回 `NotSticky`，不在开机或后台自动恢复授权。

不请求录音、读取通话状态或全部文件访问权限。通话检测通过 AudioManager 模式进行，属于尽力处理；厂商行为需真机验证。系统的屏幕授权弹窗不能由软件跳过。

## 已验证与待验证

已在独立 .NET 10 Android/API 36 工程，使用 Microsoft 的 Media3 1.11.0 绑定编译通过。编译证明 API 与绑定匹配，不能替代运行验收。

Android 15 x86_64 模拟器已实测整屏授权和真实截图、持续捕获、横屏尺寸回调、锁屏释放及解锁后保留手动模式。捕获时前台服务类型为 `0x22`，锁屏后系统投屏会话为空、服务降为媒体播放 `0x02`，无崩溃记录。这里验证的是模拟器系统接入，尚未覆盖真实游戏、vivo 内外屏、游戏音频竞争和长时间负载。

同一模拟器已验证系统悬浮授权、桌面上的悬浮显示/拖动/展开/隐藏，展开后面板自动约束在屏幕内；也已验证导入的第 3 章台词在前台和返回桌面后保持 `AudioTrack state:started`。这没有验证真实游戏的声音竞争或听感，完整证据和未测项见 `reports/android-platform-smoke.json`。

发布前至少验证：Android 10 与当前系统的授权/取消/通知停止；真实战双画面能否捕获；X Fold5 内外屏和横竖屏；两种声音策略下游戏是否暂停或降音量；有线耳机/蓝牙切换、来电和 VoIP；连续使用的原生内存、耗电及发热；后台被厂商终止后进度是否正常恢复。拒绝任何非必要权限时，手动播放必须仍可用。

主发布包使用 `arm64-v8a`，模拟器使用 `x86_64`；它们的原生运行库必须分别检查。`android/tools/check_apk.py` 验证 APK 内 ELF 的 16 KB 加载对齐及未压缩原生库的 ZIP 对齐，可同时调用 SDK 的 `zipalign`。通过检查表示打包满足这些对齐条件，不表示已在 16 KB 内核真机完成全部运行测试。

官方资料：[MediaProjection](https://developer.android.com/media/grow/media-projection)、[前台服务类型](https://developer.android.com/develop/background-work/services/fgs/service-types)、[音频焦点](https://developer.android.com/media/optimize/audio-focus)、[16 KB 页兼容](https://developer.android.com/guide/practices/page-sizes)。
