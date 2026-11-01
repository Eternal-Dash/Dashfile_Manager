# 双轨快传来源与修改

原项目：[HybridFileXfer（多轨快传，原双轨快传）](https://github.com/weixiansen574/HybridFileXfer)，原作者 **weixiansen574**。
参考版本：`14e75ab30b921b845591e49580cffcb51f3d4b9a`。原项目与本适配均采用 GPL-3.0-or-later，原许可证见 ThirdParty/HybridFileXfer-LICENSE.txt。

本适配由 EternalDash 编写：将原项目 1 MiB 分块、多路动态取块及随机访问合并思路适配到 C# 与独立 Java 设备服务。新增由电脑发起的协议 DFHFX003、证书固定 TLS 1.2、随机会话认证、设备与 Root 身份绑定、源文件快照检查、SHA-256 校验、原子替换及失败临时文件清理。不是原项目官方插件，也不与原项目 APK/PC 客户端进行协议互通。

完整对应源码位于 https://github.com/Eternal-Dash/Dashfile_Manager 的 samples/HybridTransferPlugin，设备源码 Agent/Agent.java 随包提供。构建方法：运行 build/build-hybrid-agent.ps1 后使用 .NET 8 构建 HybridTransferPlugin.csproj；D8 9.5.28 与 JDK 21 仅为构建工具，不随插件分发。未发布的本地验收版本应连同对应源码包交付。

设备要求：Android 8.0+、已授权 ADB、USB 连接；第二通道要求同一局域网可达。无需安装 APK。服务仅处理普通文件，拒绝 /dev、/proc、/sys 路径，不用于分区回读。每个通道约 1 MiB 缓冲，当前最多 3 路；文件逐一处理。上传/下载最长 30 分钟，服务空闲约 3 分钟退出。网络隔离或路由不可达时真实显示 USB 单通道。检测按钮与首次文件传输都会启动临时服务。

文件完成后计算双端 SHA-256，随后才替换目标文件。传输期间源文件变化、权限不足、取消或断线会报错，原目标保留；提交完成但确认包丢失时，目标可能已经更新，需用户检查，插件不会静默重试。

1.0.1 DashFile 改动：传输服务启动前检查单文件大小，默认 1 GB 起接管，详情页以 MB/GB 调整并保存；小文件及大小不可确定时使用宿主 ADB 后端，不更改上游代码或通信协议。

1.0.2 DashFile 改动：修复取消/ABORT 与通道并发写入导致半文件被重新创建的竞态；上传 ID 在客户端生成，取消保留可追踪路径，控制连接失败后通过 ADB 复核清理。宿主 1.2.1 提供统一传输弹窗与清理屏障。普通文件的完整提交可能在取消请求到达时已完成，此时保留完整文件。

复用闲置连接前校验服务存活，失效时仅在文件数据开始前重新连接。
