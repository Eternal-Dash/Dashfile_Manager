# 双轨快传插件

DashFile ≥ 1.2.0 / API v3，安装启用后默认接管设备文件上传、下载、拖入及拖出导出。

在插件页可关闭接管、启用/关闭局域网叠加、检测通道或取消。USB 必须连接并授权 ADB；Android 8.0+；同一局域网 Wi-Fi 可达时自动叠加，不安装 APK。只处理普通文件，分区备份使用分区页专用能力。

采用 1 MiB 有界分块、随机访问合并与 SHA-256，TLS 固定会话证书；目标用临时文件校验后原子提交。原子替换提交后确认丢失时可能实际已经完成，插件不自动重传。KernelSU 策略目录由宿主保留 staging + 原地 cp，不保证原子覆盖。

原项目 **HybridFileXfer（多轨快传，原双轨快传）**；原作者 **weixiansen574**。https://github.com/weixiansen574/HybridFileXfer 。GPL-3.0-or-later；DashFile 适配由 EternalDash 编写，协议与原项目不互通。原始许可、固定来源版本、改动及完整构建说明见 [UPSTREAM.md](UPSTREAM.md)。

构建：先运行 build/build-hybrid-agent.ps1（JDK21 + D8 9.5.28），然后 build/build-plugins.ps1。测试：tests/HybridTransferTests（默认仅 JVM/loopback；真实设备测试需显式 --device-test 参数）。
