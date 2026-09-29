# 当前句重新识别回归

纯主机测试直接引用共享核心，验证小动画令画面变化时，完整可靠的当前句可保持等待而不重播；保留下一句精确匹配、连续两次、同文手动、角色和路线边界规则。

```powershell
& 'dotnet' run --project android/Tests/SubtitleRegions/FollowCurrent/FollowCurrentRegression.csproj
```

该测试不代替真机 OCR 与音频验证。
