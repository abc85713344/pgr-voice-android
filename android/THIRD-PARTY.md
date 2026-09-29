# 安卓版第三方组件与许可

本清单对应当前固定依赖。版本来自 `PgrVoice.Android.csproj`、`packages.lock.json`、已恢复 NuGet 包的 `.nuspec` 与 Android 运行时包；简短标签不能代替各组件的原始许可和附加条款。

| 组件 | 当前使用版本 | 许可与来源 |
|---|---|---|
| .NET / Mono Android 运行时 | `Microsoft.NETCore.App.Runtime.Mono.android-*` 10.0.12 | MIT；随包第三方材料保留各自许可。[源码](https://github.com/dotnet/runtime)、[本次运行时包记录的源码](https://github.com/dotnet/dotnet/tree/95017c711e6afc1085133d440e42b4bd78155701) |
| .NET for Android 运行支持 | `Microsoft.Android.Runtime.Mono.36.android-*` 36.1.2，目标 API 36 | MIT 与原始第三方通知。[源码](https://github.com/dotnet/android) |
| AndroidX Media3 / ExoPlayer | Java 1.11.0；`Xamarin.AndroidX.Media3.ExoPlayer` 1.11.0 | Media3 为 Apache-2.0，Microsoft 绑定为 MIT；NuGet 声明 `MIT AND Apache-2.0`。[Media3 源码](https://github.com/androidx/media)、[绑定源码](https://github.com/dotnet/android-libraries) |
| ONNX Runtime | `Microsoft.ML.OnnxRuntime` / `.Managed` 1.30.0 | MIT，与包内第三方通知共同保留。[源码](https://github.com/microsoft/onnxruntime/tree/f2c39fe2f838cf35ce7da92824f5a5e3ee6e88a7) |
| PP-OCRv5 mobile 模型 | 检测与识别模型；RapidOCR v3.7.0 模型发布的 ONNX 文件 | PaddleOCR 与 RapidOCR 项目为 Apache-2.0；下载地址、文件长度与 SHA-256 见模型清单。[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)、[RapidOCR](https://github.com/RapidAI/RapidOCR) |
| Google ML Kit 中文文字识别 | `com.google.mlkit:text-recognition-chinese` 16.0.1；Microsoft 绑定 116.0.1.8 | **Google 原生 SDK 与模型遵循 ML Kit 服务条款，不能标为 Apache-2.0 或 MIT。** Microsoft 的 C# 绑定层为 MIT。[Google SDK 文档](https://developers.google.com/ml-kit/vision/text-recognition/v2/android)、[ML Kit 条款](https://developers.google.com/ml-kit/terms)、[绑定源码](https://github.com/dotnet/android-libraries) |
| 其余 AndroidX、Google、Kotlin 相关依赖 | 各包精确版本见机器清单 | 逐包保留 NuGet 许可表达式、Microsoft 绑定许可和上游通知。Google 专有 SDK 条款不因采用开源绑定而改变。 |

模型来源、OCR 前后处理引用、修改内容、版权声明和区别见 [THIRD-PARTY-OCR.md](THIRD-PARTY-OCR.md)。本实现的 OCR 部分引用的 `overlay-translator` 文件为 Apache-2.0，具体提交与修改记录已列在该文档；没有把其他仅用于调研的软件许可证套用到本项目。

## 随包通知与机器清单

- [Assets/legal/dependency-licenses.json](Assets/legal/dependency-licenses.json) 记录 97 个已解析 NuGet 依赖和 4 个 ARM64/x86_64 运行时包，包括版本、许可元数据、上游仓库/提交、Maven 坐标、NuGet 内容哈希与原始通知文件哈希。清单包含构建期依赖，不声称每个条目都作为运行代码装进 APK。
- [Assets/legal/texts](Assets/legal/texts) 保留从实际依赖包复制的原始 LICENSE / NOTICE 文本，按内容哈希去重；未翻译或改写这些文本。最终 APK 中它们位于 `assets/legal/`。
- OCR 模型与相关组件的原始许可、ONNX Runtime 第三方通知和模型校验清单位于 `Assets/ocr/`。保留这些材料，不能用此汇总替换。
- 构建使用 .NET SDK 10.0.401；SDK 本身是开发工具，不打进 APK。APK 的具体原生库列表和 16 KB 对齐验收独立记录于兼容性报告。

两个旧依赖只提供已失效的 NuGet 许可链接：构建工具 `Xamarin.Build.Download` 0.11.4 与空引用占位包 `Xamarin.Google.Guava.ListenableFuture` 9999.0.0。机器清单如实保留原链接、仓库/提交与随包通知，未凭相邻包的 MIT 标签替它们补造授权。后者的实际 Java 类型由 Guava 提供；对应 Guava 的 Apache-2.0 通知也随依赖保留。更新这些依赖时应重新核对其包元数据。

## ML Kit 条款与隐私边界

0.3.0 听书功能参考了 Voice、Smart AudioBook Player 与 Audiobookshelf 的公开功能与交互资料，未复制其源码或引入这些项目的软件组件。出处和参考范围见 `docs/听书功能参考与设计.md`；实际播放复用本项目既有 Media3 ExoPlayer，并使用 Android 系统媒体会话 API。

ML Kit 的 Google 原生 SDK 及模型受到 [ML Kit 条款](https://developers.google.com/ml-kit/terms) 和其引用的 [Google APIs 条款](https://developers.google.com/terms) 约束；模型属于相关软件。Microsoft 提供的绑定许可只覆盖绑定层。

Google 文档说明，输入图像和识别输出在设备上处理，ML Kit 不把它们发送到 Google 服务器；SDK 可能联系服务器取得更新或发送性能及使用指标。因此，本项目可说明“截图与文字识别在本机处理”，不能仅凭随包模型就推导“SDK 永不联网”。是否能联网还取决于最终 APK 的网络权限及产品设置；分发说明应与最终构建保持一致。

当前工程 Manifest 主动移除 `INTERNET` 和 `ACCESS_NETWORK_STATE`；当前合并的 Debug Manifest 也已确认不包含它们。最终发布包仍应复核合并权限，并验证两种 OCR 在首次离线启动时均可用。

本清单只涉及软件、模型及引用代码。用户导入的剧情、角色形象、文本和配音包具有各自的来源及权利安排，不由这些第三方软件许可证重新授权。
