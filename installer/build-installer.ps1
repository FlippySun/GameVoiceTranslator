# 生成安装包：发布自带运行环境的程序，再用 Inno Setup 打包。
# 用法：powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Iscc 路径]
param([string]$Iscc = "D:\Projects\_tools\InnoSetup\ISCC.exe")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pub = Join-Path $PSScriptRoot "publish"
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
dotnet publish "$root\src\GVT.App" -c Release -r win-x64 --self-contained true -o $pub -v q
dotnet publish "$root\src\GVT.Watchdog" -c Release -r win-x64 --self-contained true -o $pub -v q
Get-ChildItem $pub -Filter *.pdb | Remove-Item
$ver = (Get-Item "$pub\GameVoiceTranslator.exe").VersionInfo.ProductVersion
& $Iscc "/DAppVersion=$ver" "$PSScriptRoot\GameVoiceTranslator.iss"
