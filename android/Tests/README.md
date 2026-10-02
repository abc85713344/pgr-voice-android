# 显示形态与字幕框回归

`DisplayProfileRegression.csproj` 直接编译生产 `DisplayRegionProfile.cs`、`AppSettings.cs`，覆盖 12 组物理屏幕区分、方向、旧配置、返回原屏幕、旧帧／旧框选会话和缺失显示信息用例。测试程序以 `.cs.txt` 保存，避免被 Android 主项目默认收录。

统一构建阶段可运行：

```powershell
& '<AndroidRoot>\runtime\dotnet\dotnet.exe' run --project 'android/Tests/DisplayProfileRegression.csproj' -p:BaseIntermediateOutputPath=<AndroidRoot>/temp/display-profile-regression/obj/ -p:MSBuildProjectExtensionsPath=<AndroidRoot>/temp/display-profile-regression/obj/ -p:OutputPath=<AndroidRoot>/temp/display-profile-regression/bin/
```

生产代码使用 `Display.GetMode().PhysicalWidth/PhysicalHeight`、显示标识与旋转方向。物理显示监听与服务配置变化均触发检查；帧读取也复核形态。`MediaProjection` 的输出尺寸不参与屏幕身份判定，因此两个物理屏幕的单应用共享均为 16:9 时仍可分开。屏幕变化使自动跟随失效，必须重新确认；取图到保存之间切屏会拒绝旧框选。旧版四类比例配置保留在 `Regions`，但不自动套用；重新框选后保存到 `DisplayRegions`。

Android API 依据：[Display.Mode 物理模式尺寸](https://developer.android.com/reference/android/view/Display.Mode#getPhysicalWidth())、[Display.GetMode](https://developer.android.com/reference/android/view/Display#getMode())。不要改用 `WindowMetrics` 或捕获图尺寸来识别内外屏，因为应用窗口可能被缩放或保持固定比例。

纯逻辑回归不能验证 vivo 驱动是否及时更新显示模式或发送回调。真机仍需测试：内外屏都选择同一 16:9 应用共享，分别设置不同相对字幕框；反复折叠、返回旧屏幕并旋转；检查 `diagnostics/events.log` 的物理形态键和版本、切换后暂停、旧 OCR 不推进及原框找回。不得将普通模拟器旋转写成折叠真机验收。

本次修改按协调要求未在准备阶段执行 Android 构建、签名或设备操作；结果需在统一构建／验证后记录。
