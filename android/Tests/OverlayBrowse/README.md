# 悬浮浏览与上次 OCR 原文复核回归

21 项主机测试直接编译生产 `OverlayBrowseModels.cs`、`AppSession.OverlayBrowse.cs`、`AppSession.OcrReview.cs` 与共享核心源码。生产文件均通过项目相对路径引用；此目录不保存生产代码副本、模型或章节音频。

`Stub.cs.txt` 提供 AppSession 其余部分的测试替身：主线程同步执行、记录音频请求与模型刷新、简化外围命令和失效入口。实际浏览动作、令牌验证、OCR 证据保存和重新匹配使用生产实现。测试不覆盖 Android 窗口、真实主线程、原 AppSession 接线或游戏点击，不应作为真机端到端验证。

从播放器项目根目录执行，需要 .NET 10。构建输出必须放到工程目录外，避免嵌套 `obj` 被其他工程的默认源文件扫描包含：

```powershell
$env:DOTNET_ROOT='.android-root\runtime\dotnet'
$env:DOTNET_CLI_HOME='.android-root\dotnet-user'
$env:NUGET_PACKAGES='.android-root\nuget'
& 'dotnet' run `
  --project android\Tests\OverlayBrowse\OverlayBrowseRegression.csproj `
  --artifacts-path '.artifacts\overlay-browse-regression\artifacts' `
  -- reports\overlay-pages-034\host-browse-ocr-review.json
```

已配置其他 .NET 10 的机器可将可执行文件替换为 `dotnet`，并将 `--artifacts-path` 指向该机器的外部临时目录。报告位置是第一个应用参数；省略时默认为当前工作目录下的 `reports/overlay-pages-034/host-browse-ocr-review.json`。退出码 0 表示全通过，1 表示有失败。运行时的书签文件只写入系统临时目录中的随机测试子目录，不读写真实用户存档。

覆盖范围：

- 悬浮台词、候选、分支和记录；翻阅小节不移动进度；书签和最近 40 条履历静音恢复。
- 旧 revision、重复确认、跨 Engine 或被替换候选拒绝；隐藏页不重建整节列表。
- 原文和位置展示；清候选或变更跟随代次后仍可人工复核保存证据。
- 同包编号但不同 Engine 拒绝；原小节失效、原文错配、没有证据或零匹配不采用当前位置。
- 保存 OCR 文字与坐标的深拷贝；准备候选阶段不播放，第二步明确选句确认后才播放。
