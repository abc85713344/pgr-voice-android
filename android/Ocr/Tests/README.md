# OCR 验收与可重复测试

## 纯 C# 回归

测试工程直接编译生产使用的 `PaddleTensorCodec.cs` 和 `DbPostprocessor.cs`，不复制算法（作者本机的 PowerShell 运行脚本未包含在本仓库，用 `dotnet run` 跑对应的回归工程即可）。

当前 15 项通过：内置字典 Unicode/空白保留、blank/空格索引、重复字 CTC、类别不匹配拒绝、BGR 通道、归一化与 padding、窄长区域尺寸上限、检测旋转框/坐标映射、倾斜字幕、候选限额、弱置信度及非有限概率拒绝、取消及图像边界。

另有调度回归工程验证识别调度与停止卸载。独立项目直接编译生产调度、模型生命周期和共享剧情规则，不构建安卓项目，也不占用共享核心工程的 obj。2026-09-28 的 18 组均通过，报告为 `reports/android-ocr-scheduling.json`：两次精确确认、模糊后继续确认、固定画面抑制、画面／epoch 变化、手动定位、错误与截断重试、500／1000ms 边界、旧结果隔离，以及初始化／推理途中停止和立即重启的释放竞态；另验证空闲时停止不阻塞调用线程，以及后台释放尚未完成时旧排队请求已失效。十分钟固定字幕用可控逻辑时钟推进，不是真实安卓运行、耗电或模型性能测试。

## 安卓完整链路

1. 构建并安装 Debug x86_64 APK，启动 Android15_Test。需要真正断网时，在安装和首次启动前启用模拟器飞行模式。
2. 用设备测试脚本按章节跑（`--chapter 29` 或 `--chapter 03`）。指定 `--engine paddle` / `--engine mlkit` 可只测一个引擎。
   （该脚本是作者本机的设备测试工具，未包含在本仓库；结果记录仍保留在 `Results/` 下。）
   指定 `--serial emulator-5556 --case ch29-real-00030 --output Results/Android10` 可独立复测 API 29 单张图片，避免覆盖 API 35 结果。
3. 测试脚本把原始 PNG 经 `adb push` 复制到应用私有目录，调用仅 Debug 提供的 OCR 验收 Activity，每图识别两次。每次 Activity 先终止上一轮进程，避免将内存中热模型误记为初始化性能。
4. 结果在 `Results` 中，包含初始化时间、每次真实推理时间、文本、置信度、检测框和是否截断。脚本逐项检查人工标注短语，任何缺字/错字仍按失败记录。

验收 Activity：`cn.pgrvoice.player/org.pgrvoice.player.OcrDiagnosticActivity`；参数 `input` 是私有 files 目录下的 PNG，`engine` 为 `paddle` / `mlkit`，`report` 为输出 JSON 文件名，`iterations` 为 1–5。

`--ez maskOnly true` 可测试 Android 实际缩放生成的字幕掩码：读取私有 files 目录中的 `ch29-sequence-??.png`，输出 384×96 的 Base64 掩码。`--ez maskTight true` 使用从真实帧核实的窄字幕框（x .17–.92 / y .77–.92）；否则使用原先宽框。这个入口只在 Debug APK 中存在，Release 不导出。

## 图像来源与标注

- 第 29 章真实视频截图：现有 `reports/ocr-samples/frame-00030.0.png`、`frame-00300.0.png`、`frame-07000.0.png`；人工短语分别为“那是文明的火种”“孩子”“他被红潮侵蚀了”。真实分辨率 852×480。
- 第 3 章：本地全剧情流程视频的 10 / 15 / 25 秒原始帧，人工核对“露娜小姐已经到了”“你又做了多余的事情”“我只需要保护露娜小姐”。
- 连续帧：第 29 章长源视频的 6998.0–7002.0 秒，每 200 毫秒提一帧。6998.0–6998.6 为上一句，6998.8 是下一句逐字显示中，6999.0–7001.8 为同一句完整字幕，7002.0 已开始下一句。
- `Fixtures/sources.json` 记录新增原始帧来源、源时间和尺寸；contact 图片仅供目视核对。未以 OCR 输出自动生成真值。

## 已观察结果与限制

2026-09-28，Android 15 / API 35 AOSP x86_64 模拟器，无 Google Play 服务，4 CPU / 4 GB 虚拟内存，飞行模式下安装后首次启动：Paddle 与 ML Kit 均完成真实本地识别。第 29 章三张图的人工短语命中为 Paddle 3/3，ML Kit 1/3；ML Kit 将“文明”识别为“女明”、“孩子”识别为“该子”。不以对照结果降低自动推进门槛。

第 3 章 10 / 15 / 25 秒真实视频帧也已在同一模拟器完成：两个引擎均 3/3 人工短语命中，重复运行结果一致。六张固定图总计为 Paddle 6/6、ML Kit 4/6；这是有限样本的短语检查，不是完整字准确率或普遍准确率承诺。

Paddle 三图第二次识别约 247 / 346 / 375 毫秒；ML Kit 约 330 / 329 / 587 毫秒。这是模拟器整图识别样本，不能作为手机性能、持续耗电或最低配置证明。JSON 中托管/原生分配量也不等于整个应用 PSS 或峰值内存。

字幕稳定检测的桌面近似结果：宽区域包含 NEXT 动画和背景，384×96 同句差异可达 49 像素；从实图核实后只圈字幕，同句差异为 0–7，而换句超过 1,200。`mask-*-analysis.json` 中 PIL 双线性分析不是 Android Skia 逐像素等价结果；安卓实际掩码使用单独报告。自动跟随应先由用户框选实际字幕，不能把固定默认区域视为对所有剧情适用。

随后已完成 Android Bitmap 实際采样（`mask-android-tight.json` 及摘要）：窄区域 21 连续帧同句差异仅 0–2 像素，6998.8 秒换句差异 1,371，6999.0 秒逐字补全差异 571，7002.0 秒换句差异 1,835。按核心默认 8 像素 / 8% 白字并集 / 24 像素强变化门槛，在 6999.4 秒判定完整台词稳定，并在下一句立即复位。相同 Android 采样的宽区域同句差异达 21–50，不能稳定排除 NEXT 动画；必须先框选实际字幕区域。

## 原生库重复警告核查

Debug 和 Release 构建中的 `lp/148`（`text-recognition-bundled-common.aar`）与 `lp/181`（`textrecognitionbundledcommon-17.0.0.aar`）重复提供 `libmlkit_google_ocr_pipeline.so`。已逐文件核查，四个 ABI 的对应文件 SHA-256 完全一致，属于同一原生库的重复打包警告。完整路径、字节数、哈希在 `Results/mlkit-native-duplicate-hashes.json`。

- ARM64：11,064,544 字节，`fa41d509a661e8aa8b3e3ac1a28541f8823f308414fcad733fe26d61feea2646`。
- x86_64：11,626,128 字节，`3ddffb498b60f7f9512e341d4ee3abe4e1580c4fd65ef9b2c8ce9e490de467d8`。

已对 Debug APK 中 x86_64 的 `libonnxruntime.so`、`libonnxruntime4j_jni.so`、`libmlkit_google_ocr_pipeline.so` 检查 ELF LOAD 段，全部 `p_align=0x4000`。正式 ARM64 包的整体原生库及 ZIP 对齐另由发布校验记录确认。

这些测试不包含真实游戏并行运行、真实手机声音混合、内外屏切换或 30 分钟持续测试。

## Android 10 / API 29 补充

官方 AOSP x86_64 revision 8、2 核 / 2 GB / 720×1280，无 Google Play 服务，网络关闭后已执行第 29 章 30 秒截图的两个引擎各两轮。Paddle 运行及目标短语均通过；ML Kit 本地运行通过，但仍将“文明”识别为“女明”，短语断言失败。原始报告在 `Results/Android10`。

同一构建还通过冷启动、SAF 中文 ZIP 导入、AudioTrack 实际输出与重播、Android 10 系统捕获授权、61 帧读取 / 1 次手动屏幕 OCR / 0 错误、SAF 诊断导出及停止捕获。完整构建哈希、证据及未覆盖项目见 `Results/Android10/compatibility.json` 和 `android/docs/实际测试范围.md`。测试后正常关闭独立的 `Android10_Test`，主模拟器未改。

