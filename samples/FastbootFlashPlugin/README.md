# Fastboot刷写插件

适用：DashFile 1.1.0 及以上（API v2）。插件 ID `com.dashfile.fastbootflash`，版本 1.0.1，许可证 GPL-3.0-or-later。

安装后顶部显示“分区刷写”，位置在“设置”前。插件管理页仍可查看、启停和卸载。原有应用页面排版保持不变，插件使用宿主浅色/深色资源；左侧分区表、右侧可折叠操作区。

## 能力

- 检测 Fastboot 和已授权 ADB 设备，明确选择序列号；读取 product、槽位、解锁状态、fastbootd 状态与分区列表。
- 单分区写入：原始镜像 / Android sparse；当前槽或 A/B 指定槽，镜像展开大小与目标容量检查，SHA-256 计划预览。
- 整包：AOSP `fastboot update` ZIP、平铺 `.img` 目录、本插件备份目录和 `.dfbackup.zip`。目录中镜像按文件名匹配分区，例如 `boot.img` 根据槽位解析，`boot_a.img` 明确指定 a。
- 回读：Fastboot 原生 `fetch`；或已授权且 shell 有 Root 的 ADB，通过 `exec-out su -c dd` 流式输出原始字节。支持勾选、全部已枚举分区、目录清单与可选 ZIP。
- 每行独立选镜像、批量生成刷写计划；任何勾选行缺少镜像时整批拒绝，不静默跳过。
- 擦除勾选分区：同样校验当前设备、锁定状态、逻辑分区模式与关键分区；输入 `ERASE <序列号>` 并最终确认后才执行。
- 诊断查询：`getvar <变量或 all>` / `oem device-info`；禁止从该入口发送写入、擦除、解锁、设备重选或脚本参数。
- 日志导出、逐项进度、停止后续任务；明确确认后切换 系统 / Fastbootd / Fastboot / Recovery / 9008(EDL)。EDL 属厂商扩展，仅部分设备支持。

## 使用

1. 自动模式只选择唯一设备；发现多台设备时必须改为“手动指定设备”。点“检测设备”后明确目标，再点“读取分区”。宿主顶部的 ADB 选择器与插件目标相互独立。
2. 刷写：选择目标与文件，先生成预览。核对序列号、机型、分区、槽位、文件与哈希。执行时输入 `FLASH <序列号>` 并最终确认。
3. 回读：在“回读与备份选项”选择输出目录，在“基础操作”回读勾选分区，或选择全部。输出写入新建时间戳目录，不覆盖旧备份。ZIP 默认不压缩，以降低 CPU 开销。
4. 恢复：在“固件包”中选择原始备份目录或 `.dfbackup.zip`。要求完整清单、同 product、同序列号；逐镜像复核长度与 SHA-256，然后生成刷写计划。

## 格式与限制

- “全部回读”只覆盖设备返回的可见分区，不是厂商完整 ROM，不覆盖未暴露区域。设备没有返回分区表时，不能自动完整备份。
- `fetch` 并非所有 Bootloader 都实现；AOSP 文档描述的场景本身也有限制。插件不会伪造通用回读能力。失败后停止；切换 ADB Root 需要用户明确操作和设备授权。
- 运行中的 Android 会修改已挂载分区；长度/哈希校验只能证明所保存的文件，不代表跨分区一致性快照。重要备份建议在支持 Root 的 Recovery、分区未挂载时进行。
- Fastboot 刷写要求 Bootloader 解锁；明确锁定时禁止写入。未报告状态时需要手动确认。逻辑分区需 fastbootd。设备/驱动兼容性仍需实机验证。
- 原生 ZIP 必须含根目录 `android-info.txt` 和镜像。允许受限的 fastboot-info 原生任务，不执行厂商 `.bat/.sh`。OTA `payload.bin`、OFP、OZIP、EDL 等格式不直接支持。
- ZIP update 总是要求“允许关键/数据分区”。其内部任务由原生 Fastboot 校验和执行；不能保证对所有机型可用。预览展示 requirements、包内任务/镜像与整个 ZIP 的 SHA-256，不承诺刷前知道原生命令的每个内部步骤。
- 插件不发送解锁、格式化、AVB 绕过或活动槽切换命令。擦除和重启只在各自专用入口明确确认后执行，刷写完成不自动系统重启。原生 update 可能内部切换 fastbootd、重建动态分区，且固件写入本身可能影响数据。
- 写入进程不设置超时、不强制终止。“停止后续任务”在当前命令结束后生效；原生 update 是一个命令，可能内部写多个分区。异常/断线则停止后续步骤，不能回滚已写入内容。
- 正常关闭和插件生命周期操作会在 busy 时被宿主阻止。跨实例文件锁约束本插件刷写/回读/擦除/重启/查询；无法约束外部终端或其他工具，也无法阻止强制结束进程、系统断电或拔线。

## 文件与性能

插件数据目录：`%LOCALAPPDATA%\DashFile\plugin-data\com.dashfile.fastbootflash`（取决于宿主数据根目录）。默认回读在 `backups/`；最近一次诊断记录在 `logs/latest.log`，界面日志有长度上限。备份 ZIP 解包缓存位于 `staging/`，确认不再使用后可手动删除。日志包含设备序列号与本地文件路径，分享前自行检查。

回读和哈希采用异步流，不将分区镜像整体读入内存；ZIP 不压缩，任务串行，图片/卡片没有额外模糊动画。进度按完成分区数显示，不伪造字节速度或剩余时间。未做真机帧率基准测试。

`manifest.json` 使用 `DashFile.PartitionBackup/1`，记录 product、serial、Requested、Entries（Name/File/Size/Sha256/Logical）、覆盖范围、Complete/Error。失败保留 `.partial` 与不完整清单，整包恢复拒绝此类清单。

## 构建与测试

```powershell
dotnet build samples/FastbootFlashPlugin/FastbootFlashPlugin.csproj -c Release
dotnet run --project tests/FastbootFlashTests -c Release
dotnet run --project tests/FastbootFlashUiTests -c Release
dotnet run --project tests/DashFileTests
powershell -File build/build-plugins.ps1
```

本次专项测试 86 项、UI 检查 27 项、宿主回归 313 项通过。引擎测试使用模拟 IToolRunner 和测试进程；UI 测试使用 Avalonia Headless，不连接硬件。覆盖设备绑定、锁定状态复检、槽位/Sparse/哈希、整批预检、失败即停、软停止、备份恢复、查询参数约束、两种传输的 EDL 命令、同会话双视图、1000 行虚拟化、批量勾选通知合并、连续折叠和深浅色多尺寸布局。

重构卡死由共享 `RadioButton.GroupName` 与 `AutoDetect` / `!AutoDetect` 双向绑定的回写循环导致；最小复现捕获超过 200 次递归状态切换，并保留为 UI 回归用例。现改为单一 `DeviceModeIndex`，不再使用互相反写的单选按钮。工作区只根据父级 Viewport 调整高度，不将 DesiredSize 或 MinHeight 回写作为自身尺寸依据。折叠动效仅改变透明度、位移和箭头旋转；日志合并 UI 回调、批量勾选合并通知。

真实窗口已核对左右布局、分区信息显示和折叠控件；只读取设备信息，没有执行真实刷写、擦除、镜像回读或重启。未测真实刷写吞吐或 FPS。

## 实现位置

- `Plugin.cs`：注册页面与运行生命周期保护。
- `FlashPage.axaml` / `.cs`：响应高度的操作界面。
- `FlashViewModel.cs`：共享会话、确认、预览与串行任务。
- `FlashEngine.cs`：预检、刷写、回读、备份格式与恢复。
- `ToolRunner.cs`：明确参数列表启动程序、并行排空输出、原始字节流。
- 宿主 API v2：PluginPage 导航属性、IPluginOperationGuard；主窗口动态导航与正常关闭保护；PluginManager 生命周期保护；SegmentedTabs 支持动态增删。

## 参考

- [本项目仓库](https://github.com/Eternal-Dash/Dashfile_Manager)
- [AOSP fastbootd 与镜像回读条件](https://source.android.com/docs/core/architecture/bootloader/fastbootd)
- [AOSP Fastboot / fastboot-info 文档](https://android.googlesource.com/platform/system/core/+/refs/heads/main/fastboot/README.md)
- [AOSP Fastboot 实现](https://android.googlesource.com/platform/system/core/+/refs/heads/main/fastboot/fastboot.cpp)
- [AOSP ADB exec-out 的参数处理](https://android.googlesource.com/platform/packages/modules/adb/+/refs/heads/main/client/commandline.cpp)
