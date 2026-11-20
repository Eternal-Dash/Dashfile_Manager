# 插件下载

这里放官方插件的 `.dfp` 包。**发布包（Release 里的 zip）不含任何插件**，需要就从这个目录下，
然后在程序「插件」页点「安装 .dfp」，或直接把文件拖进插件页。

| 文件 | 插件版本 | 要求的 API | 最低宿主 | 说明 |
|---|---|---:|---|---|
| `DashFile - 双轨快传.dfp` | 1.0.4 | 3 | 1.2.0 | USB + Wi-Fi 并行传输，按大小分流，SHA-256 校验。原项目 HybridFileXfer / 原作者 weixiansen574 |
| `DashFile - 分区刷写.dfp` | 1.0.3 | 2 | 1.1.0 | 分区计划、刷写、能力检查后回读与备份、整包刷写 |
| `DashFile - AVB 工作台.dfp` | 1.0.1 | 1 | 1.0.0 | AVB 镜像解析与重签、哈希树、VBMeta 构建、整树链式 / 非链式重建、A/B 批处理、目录扫描 |
| `DashFile - Hello 示例.dfp` | 1.0.0 | 1 | 1.0.0 | 二次开发模板，可从 `samples/HelloPlugin/` 自行编译 |

> 「逆向工具箱」暂停对外提供。源码见 [samples/ReverseToolboxPlugin](../samples/ReverseToolboxPlugin/)。

## 装之前确认

- 宿主版本须达到清单的 `minHostVersion`，宿主 API 能力号须达到清单的 `apiVersion`。
- 插件与宿主共用同一份 `DashFile.PluginApi`，不要自己再打包一份；
- 插件使用可回收的 AssemblyLoadContext。禁用或卸载后，宿主移除插件功能并请求卸载程序集。

## 自己做一个

见 [插件开发指南](../docs/插件开发指南.md)，示例源码在 [samples/](../samples/)。
打包用 `build\build-plugins.ps1`，产物是 `.dfp`（zip，根目录含 `plugin.json` + 入口 DLL）。

## 署名

插件清单里的 `author` 会显示在插件页（`v版本 · 作者 · 状态`）。改写别人的插件再分发，请把原作者留在 `author` 里。
