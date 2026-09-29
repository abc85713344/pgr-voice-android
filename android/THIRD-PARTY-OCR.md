# 安卓离线 OCR 组件说明

本应用在设备上执行文字识别。PP-OCRv5 的检测、识别模型随安装包提供；中文 ML Kit 是随包提供模型的对照引擎。应用不会为 OCR 上传截图，不需要 OCR 云账号。所有屏幕坐标和识别结果仅用于播放器定位。

## PP-OCRv5 与 ONNX 模型

- 模型：PaddlePaddle / PaddleOCR 的 PP-OCRv5 mobile 检测、识别模型；ONNX 转换版本来自 RapidAI / RapidOCR v3.7.0 模型发布。
- PaddleOCR：https://github.com/PaddlePaddle/PaddleOCR ，Apache License 2.0，Copyright (c) 2020 PaddlePaddle Authors. All Rights Reserved.
- RapidOCR：https://github.com/RapidAI/RapidOCR ，Apache License 2.0。
- 模型下载来源、字节数及 SHA-256：`Assets/ocr/manifest.json`；应用首次安装解包时验证同一 SHA-256。
- 模型名称保持原样；没有再训练、量化或修改权重。识别字典直接来自 ONNX `character` 元数据，依次加入 CTC blank 和尾部空格，验证输出 18,385 类。
- 完整 PaddleOCR 许可证：`Assets/ocr/LICENSE-PaddleOCR.txt`。

## 前后处理参考代码

- 来源：https://github.com/ciddwd/overlay-translator ，Apache License 2.0。
- 引用文件：`app/src/main/java/com/gameocr/app/ocr/DBPostprocessor.kt`，核查版本 `de1cb0204c3fe548f3292d0d568dbec7c3463d11`；另参考 `PaddleOcrEngine.kt` 中 Android Bitmap/Matrix 透视裁切与 BGR 张量布局。
- `Ocr/DbPostprocessor.cs` 是经修改的 C# 移植：改为连续内存、取消检测、候选数量上限；不包含漫画列拆分；映射后钳制在截图范围。
- 其他修改：字幕区域检测大小最多 960 像素，识别高度 48、最大宽度 3200；检测遵循 PP-OCRv5 官方 ImageNet mean/std，识别遵循 0.5/0.5。未复制参考应用的翻译、云端、账号或游戏操作代码。
- 完整许可证：`Assets/ocr/LICENSE-overlay-translator.txt`。本项目不是上述项目官方产品，原作者不为本项目提供背书。

## ONNX Runtime

- Microsoft.ML.OnnxRuntime：MIT License，Copyright (c) Microsoft Corporation. All rights reserved.
- 项目：https://github.com/microsoft/onnxruntime 。精确 NuGet 版本由 `PgrVoice.Android.csproj` 固定。
- 完整许可证：`Assets/ocr/LICENSE-onnxruntime.txt`。
- 使用 CPU 推理；每个推理最多两个 intra-op 线程、一个 inter-op 线程，检测和识别串行运行；不引入 OpenCV。

## ML Kit 中文对照引擎

- Android 库：`com.google.mlkit:text-recognition-chinese:16.0.1`，采用随包模型，并非仅声明 Play Services 按需下载模型的依赖。
- 官方说明：https://developers.google.com/ml-kit/vision/text-recognition/v2/android 。
- Google ML Kit 使用条款：https://developers.google.com/ml-kit/terms 。Google 原生 SDK 不适用本项目或 Microsoft 绑定的 MIT 授权。
- Microsoft .NET 绑定 `Xamarin.Google.MLKit.TextRecognition.Chinese` 采用 MIT License；精确版本由 `PgrVoice.Android.csproj` 固定。
- 对照引擎被选中后先卸载 Paddle；同一时间只保留一个识别引擎。缺失置信度时保持 0，不伪造准确率。

## 与桌面版实现的已知区别及验证边界

- 桌面 RapidOCR 通用默认检测归一化为 0.5/0.5；本安卓实现按照 [PP-OCRv5 官方检测配置](https://github.com/PaddlePaddle/PaddleOCR/blob/main/configs/det/PP-OCRv5/PP-OCRv5_mobile_det.yml) 使用 BGR、ImageNet mean/std，因此不可声称与桌面版输出逐项完全相同。
- DB 外扩以旋转矩形为基础，使用纯 C# 几何；不等价于 OpenCV 对任意轮廓的每一个像素取舍。需要在固定字幕截图以及真机上比较识别文本、框位置、置信度与耗时。
- 模拟器识别耗时不代表骁龙 888 或 X Fold5 实际性能；真实游戏声音共存、功耗和 30 分钟稳定性仍由整机验收记录确认。
