param(
    [string]$JavaHome = "",
    [string]$R8Jar = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (!$JavaHome) { $JavaHome = Join-Path $env:USERPROFILE "dev\jdk-21" }
$javac = Join-Path $JavaHome "bin\javac.exe"
$java = Join-Path $JavaHome "bin\java.exe"
if (!(Test-Path -LiteralPath $javac)) { throw "需要 JDK 21；请通过 -JavaHome 指定安装目录。" }
$cache = Join-Path $root "artifacts\hybrid-agent"
New-Item -ItemType Directory -Force -Path $cache | Out-Null
if (!$R8Jar) {
    $R8Jar = Join-Path $cache "r8-9.5.28.jar"
    if (!(Test-Path -LiteralPath $R8Jar)) {
        Invoke-WebRequest "https://dl.google.com/dl/android/maven2/com/android/tools/r8/9.5.28/r8-9.5.28.jar" -OutFile $R8Jar
    }
}
$classes = Join-Path $cache "classes"
$dex = Join-Path $cache "dex"
New-Item -ItemType Directory -Force -Path $classes,$dex | Out-Null
$source = Join-Path $root "samples\HybridTransferPlugin\Agent\Agent.java"
& $javac -encoding UTF-8 --release 8 -Xlint:-options -d $classes $source
if ($LASTEXITCODE -ne 0) { throw "设备服务 Java 编译失败" }
$classFiles = @(Get-ChildItem -LiteralPath (Join-Path $classes "com\dashfile\hybrid") -Filter '*.class' | ForEach-Object FullName)
& $java -cp $R8Jar com.android.tools.r8.D8 --min-api 26 --lib $JavaHome --output $dex @classFiles
if ($LASTEXITCODE -ne 0) { throw "设备服务 D8 编译失败" }
Copy-Item -LiteralPath (Join-Path $dex "classes.dex") -Destination (Join-Path $root "samples\HybridTransferPlugin\Resources\agent.dex") -Force
Write-Host "agent.dex 已更新；JVM 测试可设置 HYBRID_TEST_CLASSES=$classes"
