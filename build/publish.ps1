param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\DashFile.csproj"
$out  = Join-Path $root "DashFile"
$bin  = Join-Path $root "tools\adb"

$dotnet = "dotnet"
$sdk = $env:DOTNET8_HOME
if ([string]::IsNullOrWhiteSpace($sdk)) { $sdk = Join-Path $env:USERPROFILE "dev\dotnet8" }
if (Test-Path (Join-Path $sdk "dotnet.exe")) {
    $env:DOTNET_ROOT = $sdk
    $env:PATH = "$sdk;" + $env:PATH
    $dotnet = Join-Path $sdk "dotnet.exe"
}

Write-Host "== DashFile 发布（保守版：不裁剪）==" -ForegroundColor Cyan

# 先杀掉占用：DashFile.exe 与从本目录启动的 adb.exe
Get-Process DashFile -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process adb -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($out, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (Test-Path $out) {
    for ($i = 0; $i -lt 5; $i++) {
        try { Remove-Item $out -Recurse -Force -ErrorAction Stop; break }
        catch {
            if ($i -eq 4) { throw "无法删除 $out（文件被占用）：$($_.Exception.Message)" }
            Start-Sleep -Seconds 2
        }
    }
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 体积：单文件 + 压缩 + 裁剪（partial）。EXE 从 44MB 降到约 28MB。
# 裁剪的保底项（自己的程序集 / Avalonia / Skia / CoreLib / 压缩 / 证书）写在 csproj 的 TrimmerRootAssembly 里，
# 动它们之前先读 DashFile.csproj 的注释，否则会出现 "No precompiled XAML" 或插件 MissingMethod。
& $dotnet publish $proj -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=true `
    -p:TrimMode=partial `
    -p:SatelliteResourceLanguages=zh-Hans `
    -p:PublishReadyToRun=false `
    -p:DebugType=none `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

$adbDir = Join-Path $out "Binaries\Adb"
$fbDir  = Join-Path $out "Binaries\Fastboot"
New-Item -ItemType Directory -Force -Path $adbDir, $fbDir, (Join-Path $out "Log") | Out-Null
foreach ($f in @("adb.exe","AdbWinApi.dll","AdbWinUsbApi.dll")) { Copy-Item (Join-Path $bin $f) $adbDir -Force }
Copy-Item (Join-Path $bin "fastboot.exe") $fbDir -Force

# 使用说明：从 docs 同步（若存在）
$readme = Join-Path $root "docs\使用说明.txt"
if (Test-Path $readme) { Copy-Item $readme (Join-Path $out "使用说明.txt") -Force }

# GPLv3：二进制分发必须随附许可与第三方声明
foreach ($legal in @("LICENSE", "COPYRIGHT", "THIRD-PARTY-NOTICES.md")) {
    $legalSrc = Join-Path $root $legal
    if (Test-Path $legalSrc) { Copy-Item $legalSrc $out -Force }
}
$licensesSrc = Join-Path $root "licenses"
if (Test-Path $licensesSrc) {
    $licensesOut = Join-Path $out "licenses"
    New-Item -ItemType Directory -Force -Path $licensesOut | Out-Null
    Copy-Item (Join-Path $licensesSrc "*") $licensesOut -Force
}

# 插件 SDK：外部插件编译时引用这一份 DashFile.PluginApi.dll。
# 宿主运行时会把 API 程序集加载进默认上下文，插件 ALC 直接复用它，不会出现两份类型。
$sdkDir = Join-Path $out "sdk"
New-Item -ItemType Directory -Force -Path $sdkDir | Out-Null
$apiDll = Join-Path $root "src\DashFile.PluginApi\bin\$Configuration\net8.0\DashFile.PluginApi.dll"
if (-not (Test-Path $apiDll)) {
    & $dotnet build (Join-Path $root "src\DashFile.PluginApi\DashFile.PluginApi.csproj") -c $Configuration -v q
}
if (Test-Path $apiDll) { Copy-Item $apiDll $sdkDir -Force }
$apiXml = [System.IO.Path]::ChangeExtension($apiDll, ".xml")
if (Test-Path $apiXml) {
    Copy-Item $apiXml $sdkDir -Force
    Remove-Item (Join-Path $out "DashFile.PluginApi.xml") -Force -ErrorAction SilentlyContinue
}
$pluginDoc = Join-Path $root "docs\插件开发指南.md"
if (Test-Path $pluginDoc) {
    $docsOut = Join-Path $out "docs"
    New-Item -ItemType Directory -Force -Path $docsOut | Out-Null
    Copy-Item $pluginDoc $docsOut -Force
}

# 插件目录：程序启动时也会自动创建，这里先建好方便用户直接放插件
$pluginsOut = Join-Path $out "plugins"
New-Item -ItemType Directory -Force -Path $pluginsOut | Out-Null
$pluginReadme = Join-Path $root "docs\插件目录说明.txt"
if (Test-Path $pluginReadme) { Copy-Item $pluginReadme (Join-Path $pluginsOut "README.txt") -Force }

# 插件不随发布包（发布包 plugins\ 只留 README，保持干净）：
# 这里只把插件构建到 build\plugin-dist，作为桌面插件包的来源；要装插件时整份拷进 plugins\。
$pluginBuilder = Join-Path $root "build\build-plugins.ps1"
$pluginDist = Join-Path $root "build\plugin-dist"
if (Test-Path $pluginBuilder) {
    if (Test-Path $pluginDist) { Remove-Item $pluginDist -Recurse -Force }
    & powershell -ExecutionPolicy Bypass -File $pluginBuilder -OutputDir $pluginDist -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "插件构建失败" }
    Write-Host ("== 插件包（不随发布包）：" + $pluginDist + " ==") -ForegroundColor DarkGray
}

$exeMb = [math]::Round((Get-Item (Join-Path $out "DashFile.exe")).Length/1MB,1)
$allMb = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum/1MB),1)
Write-Host ("== 完成：EXE {0} MB / 整包 {1} MB ==" -f $exeMb, $allMb) -ForegroundColor Green
