param([string]$Configuration = "Release", [switch]$NoTrim)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\DashFile.csproj"
$target = Join-Path $root "DashFile"
$out = Join-Path $root (".DashFile-publish-" + [Guid]::NewGuid().ToString("N"))
$bin  = Join-Path $root "tools\adb"

$dotnet = "dotnet"
$sdk = $env:DOTNET8_HOME
if ([string]::IsNullOrWhiteSpace($sdk)) { $sdk = Join-Path $env:USERPROFILE "dev\dotnet8" }
if (Test-Path (Join-Path $sdk "dotnet.exe")) {
    $env:DOTNET_ROOT = $sdk
    $env:PATH = "$sdk;" + $env:PATH
    $dotnet = Join-Path $sdk "dotnet.exe"
}

$trimmed = if ($NoTrim) { "false" } else { "true" }
Write-Host "== DashFile 发布（PublishTrimmed=$trimmed）==" -ForegroundColor Cyan

# 独立构建成功后再替换。绝不强制终止用户的传输/刷写任务。
New-Item -ItemType Directory -Force -Path $out | Out-Null

# 对运行时做部分裁剪，保留插件与 XAML 依赖的 API。
# 第三方插件需要额外 API 时，可用 -NoTrim 构建兼容版。
& $dotnet publish $proj -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    "-p:PublishTrimmed=$trimmed" `
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
    if ($LASTEXITCODE -ne 0) { throw "插件 SDK 构建失败" }
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
# 插件在临时目录构建，最后单独存放到 artifacts/publish-backups/<本次>/plugin-dist。
$pluginBuilder = Join-Path $root "build\build-plugins.ps1"
$pluginDist = Join-Path $out "plugin-dist"
if (Test-Path $pluginBuilder) {
    & powershell -ExecutionPolicy Bypass -File $pluginBuilder -OutputDir $pluginDist -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "插件构建失败" }
    Write-Host ("== 插件包（不随发布包）：" + $pluginDist + " ==") -ForegroundColor DarkGray
}

# 检查最终路径后才移动；保留完整旧目录，替换失败立即回滚。
$rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
$archive = Join-Path $root ("artifacts\publish-backups\" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$previous = Join-Path $archive 'DashFile'
foreach ($path in @($out, $target, $archive, $previous, $pluginDist)) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "发布路径超出项目目录：$path"
    }
    $ancestor = [IO.Path]::GetFullPath($path)
    while ($ancestor.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "发布目录不能经过链接：$ancestor"
        }
        $ancestor = Split-Path -Parent $ancestor
    }
}
$running = Get-Process DashFile -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and [string]::Equals($_.Path, (Join-Path $target 'DashFile.exe'), [StringComparison]::OrdinalIgnoreCase)
}
if ($running) { throw "正式程序仍在运行，请正常退出后重试。构建结果已保留：$out" }
if (-not (Test-Path -LiteralPath (Join-Path $out 'DashFile.exe'))) { throw '发布结果缺少 DashFile.exe' }

New-Item -ItemType Directory -Force -Path $archive | Out-Null
# 插件包独立存放，不装进纯净版。
if (Test-Path -LiteralPath $pluginDist) {
    Move-Item -LiteralPath $pluginDist -Destination (Join-Path $archive 'plugin-dist')
}
if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $previous }
try { Move-Item -LiteralPath $out -Destination $target }
catch {
    if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $target)) {
        Move-Item -LiteralPath $previous -Destination $target
    }
    throw
}
$out = $target
Write-Host ("== 旧版与独立插件包：" + $archive + " ==") -ForegroundColor DarkGray

$exeMb = [math]::Round((Get-Item (Join-Path $out "DashFile.exe")).Length/1MB,1)
$allMb = [math]::Round(((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum/1MB),1)
Write-Host ("== 完成：EXE {0} MB / 整包 {1} MB ==" -f $exeMb, $allMb) -ForegroundColor Green
