# 第三方组件与许可

DashFile 以 **GPL-3.0-or-later** 发布（见仓库根目录 `LICENSE`）。下面列出随项目使用/分发的第三方组件及其许可。完整许可文本放在 `licenses/`。

| 组件 | 版本 | 许可 | 用途 / 说明 |
|---|---|---|---|
| [HybridFileXfer](https://github.com/weixiansen574/HybridFileXfer) | 14e75ab | GPL-3.0-or-later | 双轨快传插件参考/适配；原作者 weixiansen574；原许可与修改说明在 samples/HybridTransferPlugin/ThirdParty 和 UPSTREAM.md |
| [.NET](https://github.com/dotnet/runtime) | 8.0 | MIT | 运行时（自包含发布） |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | 11.3.22 | MIT | 桌面 UI 框架 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 2.88.9 | MIT | 位图/字体渲染（含 Skia 的 BSD-3 等第三方声明，见 `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt`） |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | 2.8.x | MIT | 文字排版（随 SkiaSharp 引入） |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | MVVM 源生成器 |
| [Android SDK Platform-Tools](https://developer.android.com/tools/releases/platform-tools)（`tools/adb`） | 随仓库版本 | Apache-2.0 | 未修改的 `adb.exe` / `fastboot.exe` 及其 DLL，版权归 Google LLC |

说明：

- 本仓库**不再捆绑 Inter 字体**；界面使用系统字体（Microsoft YaHei UI / Segoe UI）。
- 所有第三方许可均与 GPLv3 兼容（MIT / Apache-2.0 均为 GPLv3 兼容许可）。
- 分发编译版（`DashFile/` 或压缩包）时，请同时提供对应源代码（本仓库）或 GPLv3 §6 要求的书面提供方式。
- 插件示例 `samples/ReverseToolboxPlugin` 与宿主使用同一 `LICENSE`（GPL-3.0-or-later）。第三方插件若与宿主组合分发，也应使用 GPLv3 兼容许可。
