# 插件下载

这里放官方插件的 `.dfp` 包。**发布包（Release 里的 zip）不含任何插件**，需要就从这个目录下，
然后在程序「插件」页点「安装 .dfp」，或直接把文件拖进插件页。

| 文件 | 插件 | 要求 | 说明 |
|---|---|---|---|
| `DashFile - 双轨快传.dfp` | 双轨快传 | 1.2.0+ | USB + Wi-Fi 并行传输，按大小分流，SHA-256 校验；原项目 HybridFileXfer / 原作者 weixiansen574 |
| `DashFile - 分区刷写.dfp` | 分区刷写 | 1.1.0+ | 分区计划、刷写、能力检查后回读与备份、整包刷写 |
| `DashFile - AVB 工作台.dfp` | AVB 工作台 | 1.0.0+ | AVB 镜像解析与重签、哈希树、VBMeta 构建、整树链式 / 非链式重建、A/B 批处理、目录扫描 |
| `DashFile - Hello 示例.dfp` | Hello 示例 | 1.0.0+ | 二次开发模板，不对外分发，需要时自行编译 |

> 「逆向工具箱」暂停对外提供，作者重做后再放回；源码在 `../samples/ReverseToolboxPlugin/`。

## 装之前确认

- 宿主版本 ≥ 插件 `plugin.json` 里的 `minHostVersion`（各插件要求不同）；
- 插件与宿主共用同一份 `DashFile.PluginApi`，不要自己再打包一份；
- 插件运行在可回收 AssemblyLoadContext 里，禁用或卸载即回收。

## 自己做一个

见 [插件开发指南](../docs/插件开发指南.md)，示例源码在 [samples/](../samples/)。
打包用 `build\build-plugins.ps1`，产物是 `.dfp`（zip，根目录含 `plugin.json` + 入口 DLL）。

## 署名

插件清单里的 `author` 会显示在插件页（`v版本 · 作者 · 状态`）。改写别人的插件再分发，请把原作者留在 `author` 里。
