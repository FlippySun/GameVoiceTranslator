# 生成安装包：发布自带运行环境的程序，再用 Inno Setup 6 打包。输出在 installer\output。
# 用法：powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Iscc ISCC.exe 路径]
param([string]$Iscc = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $Iscc) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "D:\Projects\_tools\InnoSetup\ISCC.exe"
    )
    $Iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { $Iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
    if (-not $Iscc) { throw "找不到 Inno Setup 6 的 ISCC.exe，请先安装 Inno Setup 6 或用 -Iscc 指定路径" }
}

$pub = Join-Path $PSScriptRoot "publish"
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish "$root\src\GVT.App" -c Release -r win-x64 --self-contained true -o $pub -v q
if ($LASTEXITCODE -ne 0) { throw "发布 GVT.App 失败" }
dotnet publish "$root\src\GVT.Watchdog" -c Release -r win-x64 --self-contained true -o $pub -v q
if ($LASTEXITCODE -ne 0) { throw "发布 GVT.Watchdog 失败" }
Get-ChildItem $pub -Filter *.pdb | Remove-Item
# 公开版本不带 VB-Audio 安装包（不允许再分发）
if (Test-Path "$pub\drivers") { Remove-Item "$pub\drivers" -Recurse -Force }

$ver = (Get-Item "$pub\GameVoiceTranslator.exe").VersionInfo.ProductVersion
& $Iscc "/DAppVersion=$ver" "$PSScriptRoot\GameVoiceTranslator.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 打包失败" }
