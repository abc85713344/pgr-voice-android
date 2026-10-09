# 首次识别后的顺序自动播放回归

运行：

```powershell
dotnet run --project android/Tests/AutoPlaybackCycle/AutoPlaybackCycleRegression.csproj -- reports/android-autoplay-cycle.json
```

独立工程只编译 `AutoPlaybackCycle.cs`，不构建安卓应用、不使用共享核心输出缓存。

阶段为 `Off → CheckingCurrent → Playing → Tapping → StartingNext → Playing`。只有首次 `CheckingCurrent` 需要 OCR。配音自然完成之后，不再进入逐句 OCR 状态；应用须核对已验证共同线的直接下一句，再申请一次手势。

每次启用有独立 `Epoch`；每次真实播放绑定 `AndroidAudioPlayer.PlayTagged` 返回的递增票据；每次手势另有 `TapTicket`。完成回调同时检查当前阶段、代次、票据和期待节点，消费一次后不能复用。关闭或重新启用会丢弃旧事件。

回归包含 11 组正常链路、重复回调、旧回调、错节点、停止重启与超时测试。首次识别和 `StartingNext` 的期限通过 `TimedOut(now)` 暴露给会话；会话应在进入准备播放前检查期限，并在超时后 `Stop()`。点击开始和手势完成的过期期限还由状态机内部检查。

这些是纯状态机测试，不证明音频回调确实来自自然结束，也不证明真实屏幕点击成功。共同线资格、缺音、捕获权限、前台应用、实际手势和用户手动干预由其他模块与设备测试验证。
