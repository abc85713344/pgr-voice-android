# 黑底居中旁白选区

`android/SubtitleRegionSelector.cs` 是无 Android 或图像库依赖的纯 C# 算法。

集成时将完整捕获画面双线性缩放至 `SampleWidth × SampleHeight`（320 × 192），把按行排列的 ARGB 整数传给 `TrySelectNarration`。低分辨率扫描不保存截图、不调用 OCR。返回 `false` 时沿用用户字幕框；返回 `true` 时临时使用返回的比例区域 `(X=.04, Y=.15, Width=.92, Height=.50)`，不要写回用户设置。

缩略图扫描有明确大小上限（宽 96–640、高 64–384），防止调用方误传完整大图。内部仅使用有界栈空间。范围切换时调用方须重新开始稳定检测，并拒绝原范围的过期 OCR 结果。所有文字仍须经过原有 OCR、连续识别和路线匹配，不因选区启发式而放宽自动播放规则。

算法要求大范围背景近黑、几乎无彩色像素，中央灰白亮点构成少量横向文字行、列投影有多次变化，同时底部没有普通对白。顶部菜单不计入证据，孤立进度箭头不会触发。正文旁的箭头可能仍在选区内，不能保证从宽框剪掉。这里不能从像素判断“这就是剧情文字”；黑底说明文字等相似排版也可能触发临时 OCR，是否匹配剧情交给后续门控。淡入淡出、很短的字、非黑背景或范围外旁白可能保守拒绝，用户框选仍须可用。

运行纯主机回归：

```powershell
& 'dotnet' run --project android\Tests\SubtitleRegions\SubtitleRegions.csproj -- android\Tests\SubtitleRegions\results.json
```

`fixtures/*.argb` 来自用户第 29 章第一小节截图的双线性缩略图，仅主机测试使用，不放入 APK Assets。`fixtures/provenance.json` 保存源图与夹具校验值。使用 Pillow 的 `prepare_fixture.py` 可从原截图重建夹具；执行回归本身不需要 Python。测试包含真实画面 320 × 192 和 160 × 96 的正例，以及多行、长行、纯黑、顶部菜单、箭头、底部对白、正常场景、空心按钮、噪声和无效缓冲区等边界。

这些测试仅验证选区算法；不能据此声称手机 OCR、自动跟随、语音或全部黑底剧情均已验证。

## 跨区域安全回归

`FollowRegionSwitch.cs` 使用生产 `FollowSafetyController`、`SubtitleStability` 和 `OcrInferenceSchedule`，重放 AppSession 的切区调用协议。独立项目直接编译核心源文件，不引用或重建 Android/共享核心项目，避免影响正在进行的发布构建。

```powershell
& 'dotnet' run --project android\Tests\SubtitleRegions\CrossRegion\FollowRegionSwitch.csproj -- android\Tests\SubtitleRegions\cross-region-results.json
```

7 项检查覆盖：切区保留 armed、清掉旧一次共识、新区须连续两次识别、旧 Advance 决策不能迟到应用、切回用户框也重新确认、不能自动开启已暂停的跟随、旧 OCR 槽位与休眠状态，以及真实截图的宽框稳定掩码。

真实掩码由 `prepare_mask_fixture.py` 以 Pillow 双线性近似 AppSession 的 384 × 96 灰白像素条件生成。第 29 章同一句两张原截图分别剩 53、52 个亮像素，高于 12 像素空白阈值；差异 3、并集 54，没有触发稳定重置。这只是主机证据，Android Bitmap 的实际缩放效果仍须真机验证。上述测试不执行 AppSession；跨 regionGeneration 的回调拒绝条件仅进行代码审查。
