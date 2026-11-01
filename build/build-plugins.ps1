param(
    [string]$Configuration = "Release",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root "build\plugin-dist"
}

$dotnet = "dotnet"
$sdk = $env:DOTNET8_HOME
if ([string]::IsNullOrWhiteSpace($sdk)) { $sdk = Join-Path $env:USERPROFILE "dev\dotnet8" }
if (Test-Path (Join-Path $sdk "dotnet.exe")) {
    $env:DOTNET_ROOT = $sdk
    $env:PATH = "$sdk;" + $env:PATH
    $dotnet = Join-Path $sdk "dotnet.exe"
}

# 热插拔插件：每个插件 = 一个目录 + plugin.json + 入口 dll。
# 只复制入口 dll；Avalonia / DashFile.PluginApi 由宿主提供。
# 按下列清单生成插件包；宿主依赖不重复打包。
$plugins = @(
    @{ Name = "HybridTransferPlugin"; Id = "com.dashfile.hybridtransfer"; Dll = "HybridTransferPlugin.dll" },
    @{ Name = "FastbootFlashPlugin"; Id = "com.dashfile.fastbootflash"; Dll = "FastbootFlashPlugin.dll" },
    @{ Name = "AvbStudioPlugin"; Id = "com.dashfile.avbstudio"; Dll = "AvbStudioPlugin.dll" },
    @{ Name = "ReverseToolboxPlugin"; Id = "com.dashfile.reversetool"; Dll = "ReverseToolboxPlugin.dll" },
    # 示例插件：不随仓库 plugins/ 与发布包分发，只作为二次开发的模板（手动取 buildplugin-distcom.dashfile.hello.dfp）
    @{ Name = "HelloPlugin"; Id = "com.dashfile.hello"; Dll = "HelloPlugin.dll" }
)

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

foreach ($plugin in $plugins) {
    $projectDir = Join-Path $root ("samples\" + $plugin.Name)
    $project = Join-Path $projectDir ($plugin.Name + ".csproj")
    Write-Host ("== 构建插件 " + $plugin.Id + " ==") -ForegroundColor Cyan
    & $dotnet build $project -c $Configuration -v q
    if ($LASTEXITCODE -ne 0) { throw ("插件构建失败：" + $plugin.Name) }

    $src = Join-Path $projectDir ("bin\" + $Configuration + "\net8.0\" + $plugin.Dll)
    if (-not (Test-Path $src)) { throw ("找不到插件程序集：" + $src) }

    $dst = Join-Path $OutputDir $plugin.Id
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item $src $dst -Force
    Copy-Item (Join-Path $projectDir "plugin.json") $dst -Force
    if (Test-Path (Join-Path $projectDir "README.md")) { Copy-Item (Join-Path $projectDir "README.md") $dst -Force }
    if ($plugin.Name -eq "HybridTransferPlugin") {
        Copy-Item (Join-Path $projectDir "UPSTREAM.md") $dst -Force
        Copy-Item (Join-Path $projectDir "ThirdParty") $dst -Recurse -Force
        Copy-Item (Join-Path $projectDir "Agent") $dst -Recurse -Force
    }
    # 许可证随插件一起分发（GPLv3 要求）
    Copy-Item (Join-Path $root "LICENSE") $dst -Force
    Copy-Item (Join-Path $root "COPYRIGHT") $dst -Force

    # 统一分发后缀：.dfp（zip），根目录直接是 plugin.json + 入口 DLL
    $dfp = Join-Path $OutputDir ($plugin.Id + ".dfp")
    if (Test-Path $dfp) { Remove-Item $dfp -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($dst, $dfp,
        [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host ("   -> " + $dst) -ForegroundColor DarkGray
    Write-Host ("   -> " + $dfp) -ForegroundColor DarkGray
}

Write-Host ("== 插件完成：" + $plugins.Count + " 个 -> " + $OutputDir + " ==") -ForegroundColor Green
