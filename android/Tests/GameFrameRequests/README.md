# 主动取游戏画面请求回归

直接编译生产 `PendingGameFrameRequest.cs` 和 `DisplayRegionProfile.cs`，无需 Android 或模拟器。

```powershell
& '<AndroidRoot>\runtime\dotnet\dotnet.exe' run --project android/Tests/GameFrameRequests/GameFrameRequestRegression.csproj
```

21 项回归覆盖前台等待、首次捕获初始化、后台旋转、捕获尺寸变化、显示暂不可用、静态重复采样、700 毫秒稳定等待、不可延长的 10 秒期限、显式取消、授权会话隔离和一次消费；还覆盖全程无帧时到期取消，以及请求交付后框选上下文依然拒绝旧方向、显示版本、会话和尺寸。

组件只负责尚未取帧的请求。调用方仍需在切章、切模式、停止、返回应用等明确操作时调用 `Clear()`，取得截图后继续严格校验其显示状态、捕获会话和尺寸。该回归不代表已经验证真机切应用、后台启动窗口或实际 OCR。
