# 点按跟随的手势与请求票据

`TapFollowGestureGate` 为纯 C# 单指轻点识别，调用者把 12 dp 换算成像素传入。700 ms 以内、移动距离不超过阈值、单指且同一指针才接受 Up；取消后不会重新拼接手势。连续有效轻点之间至少间隔 300 ms。Android 触摸层也检查合并 Move 的历史点。

`TapFollowRequestGate` 为独立的注入请求票据，供 AppSession 串行调用：

1. `TryBegin(nowMs, out ticket)` 成功后，先撤销点按窗口，再请求实际游戏点击。
2. 完成回调中先验证服务、游戏、显示形态等上下文，再执行 `TryComplete(ticket, succeeded, out mayAdvance)`。
3. 返回 false 表示过期或重复回调，不能恢复旧窗口或推进。返回 true 表示核销当前请求，可恢复当前窗口；只有 `mayAdvance` 为 true 才允许剧情下一句。
4. 停止、切模式、切章、重新授权或换屏时 `Invalidate()`。为当前注入暂时 `Suspend()` 触摸窗口不应使此票据失效。

窗口 `TapFollowOverlay(Context, width, height, ScreenRegion, Action<float,float>)` 仅占指定矩形；回调为 Up 的物理 `RawX/RawY` 坐标。`Show/Hide/Suspend/Resume/Dispose` 均为 UI 线程上的幂等操作，不产生触摸。`Suspend` 同步移除窗口；`Resume` 只在此前 Show 且未 Hide/Dispose 时重新挂载。Android 11 及以上关闭窗口的系统栏 inset 偏移，范围仍须由调用方绑定当前物理屏幕。

主机回归（构建输出放外部，避免嵌套 obj 进入 Android 默认源文件扫描）：

```powershell
& '<AndroidRoot>\runtime\dotnet\dotnet.exe' run --project android\Tests\TapFollowGate\TapFollowGestureTests.csproj --artifacts-path '<AndroidRoot>\temp\tap-follow-regression-20260928\artifacts' -- android\Tests\TapFollowGate\results.json
```

24 项用例覆盖时间与距离边界、对角线、拖出再返回、多指、指针替换、系统取消、重挂去重、注入进行中、失败、旧 epoch、重复/迟到完成等。它们没有运行 Android 窗口、无障碍注入或游戏，不证明游戏一定接收点击。Android 原生 API 已对本地 .NET 10 Android 引用说明检查，应用编译和设备验证由集成方执行。
