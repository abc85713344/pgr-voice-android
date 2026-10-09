# 安卓首次使用引导回归

直接编译生产 `OnboardingStore`、`OnboardingProgress`、`OnboardingPreview` 和 `AppSession.Onboarding`；替身仅隔离平台音频、仓库边界和听书打开入口。使用真实核心导航验证试听选择与打开用途不会混写游戏位置。另检查 Activity 生命周期、导入及构造顺序的真实入口接线。

由统一构建会话运行，避免与安卓打包共写核心输出：

```powershell
dotnet run --project android/Tests/Onboarding/OnboardingRegression.csproj -- 'reports/onboarding-20261008/android-host-tests.json' '<工作目录>/战双配音流程和工作/战双剧情配音播放器'
```

14 组覆盖新旧安装判定、持久恢复、跳过/关闭、权限及导入往返代次、单句自然结束、静音/缺文件/失败/中断、旧回调失效、会话暂停及完成打开语义。临时夹具写入系统临时目录，结果包含其真实位置。主机检查不能替代实际系统文件选择器、权限页面、音频设备及 Release 安装验证。
