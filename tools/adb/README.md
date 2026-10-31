# tools/adb

本目录在本地构建时存放 **Android SDK Platform-Tools**（`adb.exe`、`fastboot.exe`、
`AdbWinApi.dll`、`AdbWinUsbApi.dll`）。这些二进制**不纳入版本库**（已在 `.gitignore` 中排除），
所以刚克隆下来的仓库里这里是空的——这是预期状态，不是文件丢失。

## 为什么不入库

- 平台工具是 Google 的第三方二进制，按 Apache-2.0 分发，体积约 10 MB，随本仓库的
  GPLv3 源码一起托管既无必要也不便于审计；
- 源码仓库只保留版本、来源与放置说明，二进制由发版流程从本机复制进发布包。

## 本地补齐（构建发布包前需要）

1. 下载官方 platform-tools：
   <https://developer.android.com/tools/releases/platform-tools>
2. 解压后把下面 4 个文件放到本目录：

   ```
   tools\adb\
     adb.exe
     AdbWinApi.dll
     AdbWinUsbApi.dll
     fastboot.exe
   ```

3. 校验版本一致后再发版：

   ```powershell
   .\tools\adb\adb.exe version
   .\tools\adb\fastboot.exe --version
   ```

## 发布时如何使用

`build\publish.ps1` 会读取本目录，把 `adb.exe`、`AdbWinApi.dll`、`AdbWinUsbApi.dll`
复制到产物的 `Binaries\Adb\`，把 `fastboot.exe` 复制到 `Binaries\Fastboot\`。
因此**源码仓库不含 adb，但用户下载的发布包开箱即用**，无需自行配置环境变量。

## 许可

platform-tools 版权归 The Android Open Source Project，按 Apache License 2.0 授权，
全文见 `licenses/Apache-2.0.txt`；来源与声明同时记录在 `THIRD-PARTY-NOTICES.md`。
本仓库对其仅作原样再分发，未作修改。
