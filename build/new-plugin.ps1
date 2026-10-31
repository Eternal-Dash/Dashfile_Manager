<#
.SYNOPSIS
  生成一个 DashFile 插件骨架（csproj + Plugin.cs + plugin.json + README）。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\new-plugin.ps1 -Id com.me.dexhelper -Name DexHelper -Author "你的名字"
#>
param(
    [Parameter(Mandatory = $true)][string]$Id,
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Author = "your_name",
    [string]$Description = "一句话说明这个插件做什么",
    [string]$Tagline = "一句话亮点",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) { $OutputDir = Join-Path $root "samples" }

if ($Id -notmatch '^[A-Za-z0-9._-]+$') { throw "id 只能包含字母/数字/._-：$Id" }
$dir = Join-Path $OutputDir $Name
if (Test-Path $dir) { throw "目录已存在：$dir" }
New-Item -ItemType Directory -Force -Path $dir | Out-Null

$utf8 = New-Object System.Text.UTF8Encoding($false)

# ---- csproj ----
$csproj = @"
<Project Sdk=""Microsoft.NET.Sdk"">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AssemblyName>$Name</AssemblyName>
    <RootNamespace>$Name</RootNamespace>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <Authors>$Author</Authors>
    <Product>DashFile 插件</Product>
  </PropertyGroup>

  <ItemGroup>
    <!-- API 由宿主提供，不要把 DashFile.PluginApi.dll 复制进插件目录 -->
    <ProjectReference Include=""..\..\src\DashFile.PluginApi\DashFile.PluginApi.csproj"">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

</Project>
"@
[System.IO.File]::WriteAllText((Join-Path $dir "$Name.csproj"), $csproj.Replace("`r`n", "`n"), $utf8)

# ---- plugin.json ----
# 直接出模板文本：ConvertTo-Json 的缩进和空数组排版不适合当"给人看的清单"
# 用占位符替换，避开 PowerShell 字符串里的 $ 与反引号转义
$manifestJson = @'
{
  "$schema": "../../docs/plugin.schema.json",
  "id": "__ID__",
  "name": "__NAME__",
  "version": "1.0.0",
  "versionCode": 1000000,
  "author": "__AUTHOR__",
  "description": "__DESC__",
  "tagline": "__TAGLINE__",
  "license": "GPL-3.0-or-later",
  "tags": [],
  "icon": "◆",
  "assembly": "__NAME__.dll",
  "entryType": "__NAME__.Plugin",
  "apiVersion": 1,
  "minHostVersion": "1.0.0"
}
'@
$manifestJson = $manifestJson.Replace('__ID__', $Id).Replace('__NAME__', $Name).Replace('__AUTHOR__', $Author).Replace('__DESC__', $Description).Replace('__TAGLINE__', $Tagline)
[System.IO.File]::WriteAllText((Join-Path $dir "plugin.json"), $manifestJson, (New-Object System.Text.UTF8Encoding($false)))

# ---- Plugin.cs ----
$cs = @"
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using DashFile.PluginApi;

namespace $Name;

public sealed class Plugin : PluginBase
{
    // 下面这些会被宿主用于展示；清单 plugin.json 同名字段会覆盖这里的值
    public override PluginMetadata Metadata => new()
    {
        Id = "$Id",
        Name = "$Name",
        Version = "1.0.0",
        Author = "$Author",
        Description = "$Description",
        Tagline = "$Tagline",
    };

    protected override Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        Host.AddPage(PluginPage.FromControl("main", "$Name", BuildPage));

        // 需要右键动作时打开：
        // Host.AddAction(new PluginAction
        // {
        //     Id = "sample",
        //     Title = "$Name：做点什么",
        //     Targets = new[] { PluginTargetKind.File },
        //     ExecuteAsync = async (target, ct) => await Host.Ui.ShowMessageAsync("$Name", target.Path, ct),
        // });

        return Task.CompletedTask;
    }

    private static Control BuildPage(IPluginHost host)
    {
        return new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = "$Name", FontSize = 20 },
                new TextBlock { Text = "在这里放你自己的界面。", Opacity = 0.7 },
            },
        };
    }
}
"@
[System.IO.File]::WriteAllText((Join-Path $dir "Plugin.cs"), $cs.Replace("`r`n", "`n"), $utf8)

Write-Host ""
Write-Host "== 插件骨架已生成 ==" -ForegroundColor Green
Write-Host "  $dir"
Write-Host ""
Write-Host "下一步："
Write-Host "  1. 写你的界面/动作（Plugin.cs）"
Write-Host "  2. 改 plugin.json：id / name / author / version 由你自己定"
Write-Host "  3. 打包：把该插件加进 build\build-plugins.ps1 的 `$plugins 列表，或直接 dotnet build 后手动压 zip"
Write-Host "  4. 产物是 .dfp（zip：plugin.json + 入口 DLL + LICENSE），拖进插件页即可安装"