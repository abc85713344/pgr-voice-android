# 定位取消触摸回归

只使用纯 C# 门控模型，不加载 Android 或操作设备。

```powershell
<AndroidRoot>\runtime\dotnet\dotnet.exe run --project android/Tests/CaptureCancelGesture/CaptureCancelGestureTests.csproj -c Release
```

覆盖首次显示时间、旧 Up、跨轮、窗口重建、pointer 与 DownTime、出界、多指、取消、直接 Click、重复事件，以及有效新触摸一轮只取消一次。Android 适配层另外拒绝旧 View，合并 Move 的任一历史采样出界也会拒绝；本测试不声称验证了系统触摸分派。
