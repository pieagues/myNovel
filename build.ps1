# 构建 mynovel 的 Visual Studio 扩展（VSIX）
# 依赖：已安装的 Visual Studio 2022（提供 Roslyn csc 与 Shell SDK 程序集）+ tools/vssdk（VSCT 编译器）
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
$src = Join-Path $root 'src'
$obj = Join-Path $root 'obj'
$dist = Join-Path $root 'dist'

# 定位 Visual Studio
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = $null
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -property installationPath
}
if (-not $vs) { $vs = 'C:\Program Files\Microsoft Visual Studio\2022\Community' }

$csc = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
$publicAssemblies = Join-Path $vs 'Common7\IDE\PublicAssemblies'
$fxDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2'

# VSCT 编译器不在 VS 安装目录里，首次构建时自动从 NuGet 取 VSSDK.BuildTools
$toolsBin = Join-Path $root 'tools\vssdk\tools\vssdk\bin'
if (-not (Test-Path (Join-Path $toolsBin 'VSCT.exe'))) {
    Write-Host "[0/5] 下载 VSCT 编译器 (Microsoft.VSSDK.BuildTools)"
    $ver = '17.14.2142'
    $url = "https://api.nuget.org/v3-flatcontainer/microsoft.vssdk.buildtools/$ver/microsoft.vssdk.buildtools.$ver.nupkg"
    $zipPath = Join-Path $env:TEMP 'vssdk.buildtools.zip'
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing
    Expand-Archive -Path $zipPath -DestinationPath (Join-Path $root 'tools\vssdk') -Force
    Remove-Item $zipPath -Force
}

$vsctExe = Join-Path $toolsBin 'VSCT.exe'
$pkgdefExe = Join-Path $toolsBin 'CreatePkgDef.exe'
$vssdkInc = Join-Path $root 'tools\vssdk\tools\vssdk\inc'

foreach ($p in @($csc, $vsctExe, $pkgdefExe, $fxDir)) {
    if (-not (Test-Path $p)) { throw "缺少构建依赖：$p" }
}

Write-Host "[1/5] 清理输出目录"
if (Test-Path $obj) { [System.IO.Directory]::Delete($obj, $true) }
if (Test-Path $dist) { [System.IO.Directory]::Delete($dist, $true) }
New-Item -ItemType Directory -Force -Path $obj, $dist | Out-Null

Write-Host "[2/5] 编译命令表 (vsct -> cto)"
$cto = Join-Path $obj 'MyNovelPackage.cto'
& $vsctExe -nologo (Join-Path $src 'MyNovelPackage.vsct') $cto "-I$vssdkInc"
if ($LASTEXITCODE -ne 0) { throw "VSCT 编译失败" }

Write-Host "[3/5] 编译程序集"
# 按 VSSDK 官方 MergeCtoResource 的做法，把 cto 同时合并进 .resources 资源集
# VS 在中文界面下通过资源集查找菜单表，仅有裸嵌入资源会导致 Resource not found
$resx = Join-Path $obj 'MyNovel.resources'
$rw = New-Object System.Resources.ResourceWriter($resx)
$rw.AddResource('Menus.ctmenu', [System.IO.File]::ReadAllBytes($cto))
$rw.Close()

$refs = @(
    (Join-Path $fxDir 'System.dll'),
    (Join-Path $fxDir 'System.Core.dll'),
    (Join-Path $fxDir 'System.Xml.dll'),
    (Join-Path $fxDir 'System.Design.dll'),
    (Join-Path $fxDir 'System.Drawing.dll'),
    (Join-Path $fxDir 'System.Runtime.Serialization.dll'),
    (Join-Path $fxDir 'WindowsBase.dll'),
    (Join-Path $fxDir 'PresentationCore.dll'),
    (Join-Path $fxDir 'PresentationFramework.dll'),
    (Join-Path $fxDir 'System.Xaml.dll'),
    (Join-Path $publicAssemblies 'Microsoft.VisualStudio.Shell.15.0.dll'),
    (Join-Path $publicAssemblies 'Microsoft.VisualStudio.Shell.Framework.dll'),
    (Join-Path $publicAssemblies 'Microsoft.VisualStudio.Shell.Interop.dll'),
    (Join-Path $publicAssemblies 'Microsoft.VisualStudio.Shell.Interop.8.0.dll'),
    (Join-Path $publicAssemblies 'Microsoft.VisualStudio.OLE.Interop.dll'),
    (Join-Path $publicAssemblies 'Microsoft.VisualStudio.Interop.dll'),
    (Join-Path $publicAssemblies 'envdte.dll')
)
$dll = Join-Path $obj 'MyNovel.dll'
$sources = Get-ChildItem (Join-Path $src '*.cs') | ForEach-Object { $_.FullName }
$cscArgs = @(
    '/nologo', '/target:library', '/platform:anycpu', '/codepage:65001', '/langversion:7.3',
    ('/out:' + $dll),
    ('/resource:' + $cto + ',Menus.ctmenu'),
    ('/resource:' + $resx + ',MyNovel.resources')
) + ($refs | ForEach-Object { '/reference:' + $_ }) + $sources
& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "C# 编译失败" }

Write-Host "[4/5] 生成 pkgdef"
$pkgdef = Join-Path $obj 'MyNovel.pkgdef'
& $pkgdefExe "/out=$pkgdef" $dll
if ($LASTEXITCODE -ne 0) { throw "pkgdef 生成失败" }

Write-Host "[5/5] 打包 VSIX"
$stage = Join-Path $obj 'vsix'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item $dll $stage
Copy-Item $pkgdef $stage
Copy-Item (Join-Path $src 'source.extension.vsixmanifest') (Join-Path $stage 'extension.vsixmanifest')
Copy-Item -LiteralPath (Join-Path $src '[Content_Types].xml') $stage

$zip = Join-Path $dist 'MyNovel.zip'
# 用 ZipFile 打包，避免 Compress-Archive 把 [Content_Types].xml 当成通配符漏掉
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Move-Item $zip (Join-Path $dist 'MyNovel.vsix') -Force

Write-Host ""
Write-Host "构建完成："
Write-Host (Join-Path $dist 'MyNovel.vsix')
