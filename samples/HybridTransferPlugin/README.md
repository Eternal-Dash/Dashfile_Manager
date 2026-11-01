# 双轨快传插件

DashFile ≥ 1.2.1 / API v3，插件 1.0.2：安装启用后按单个文件大小选择设备文件上传、下载、拖入及拖出导出。默认阈值 1 GB（1024 MB），大于等于时启用双轨，小于时使用普通 ADB。

在插件页可关闭自动选择、用 MB/GB 设置阈值（最小步长 1 MB，范围 1 MB～1024 GB）、恢复 1 GB、启用/关闭局域网叠加、检测通道或取消。单位切换保留实际字节数，设置跨重启保存，对下一次传输生效。目录按每个文件分别判断，文件夹导出压缩包按实际压缩包大小判断。大小检测在临时服务启动前完成；无法判断大小时提示并使用普通 ADB。检测通道按钮不受大小阈值限制。USB 必须连接并授权 ADB；Android 8.0+；同一局域网 Wi-Fi 可达时自动叠加，不安装 APK。只处理普通文件，分区备份使用分区页专用能力。

采用 1 MiB 有界分块、随机访问合并与 SHA-256，TLS 固定会话证书；目标用临时文件校验后原子提交。原子替换提交后确认丢失时可能实际已经完成，插件不自动重传。KernelSU 策略目录由宿主保留 staging + 原地 cp，不保证原子覆盖。

原项目 **HybridFileXfer（多轨快传，原双轨快传）**；原作者 **weixiansen574**。https://github.com/weixiansen574/HybridFileXfer 。GPL-3.0-or-later；DashFile 适配由 EternalDash 编写，协议与原项目不互通。原始许可、固定来源版本、改动及完整构建说明见 [UPSTREAM.md](UPSTREAM.md)。

构建：先运行 build/build-hybrid-agent.ps1（JDK21 + D8 9.5.28），然后 build/build-plugins.ps1。测试：tests/HybridTransferTests（默认仅 JVM/loopback；真实设备测试需显式 --device-test 参数）。

1.0.2：使用宿主统一进度弹窗，取消/关闭后等待清理结束。修复并发通道在 ABORT 后重新创建临时文件的问题，设备使用 DFHFX003。
