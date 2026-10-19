param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0')

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCompiler = Join-Path $taskRoot '.tools\inno\compiler\ISCC.exe'
$taskPayload = Join-Path $taskRoot 'dist\PowerHub'
$taskOutput = Join-Path $taskRoot 'dist'
$taskScript = Join-Path $taskRoot 'installer\PowerHub.iss'

if (-not (Test-Path -LiteralPath $taskCompiler -PathType Leaf)) {
    throw "未找到 Inno Setup 编译器：$taskCompiler"
}
foreach ($required in @('PowerHub.exe', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskPayload $required) -PathType Leaf)) {
        throw "发布目录缺少 $required。请先运行 scripts\Build.ps1 -Publish。"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'Assets\powerhub.ico') -PathType Leaf)) {
    throw '缺少 Assets\powerhub.ico。请先生成应用图标。'
}

$taskBaseName = "PowerHub-Setup-$Version-x64"
& $taskCompiler "/DAppVersion=$Version" "/O$taskOutput" "/F$taskBaseName" $taskScript
if ($LASTEXITCODE -ne 0) { throw "安装器编译失败，退出码 $LASTEXITCODE。" }

$taskInstaller = Join-Path $taskOutput "$taskBaseName.exe"
if (-not (Test-Path -LiteralPath $taskInstaller -PathType Leaf)) {
    throw "编译器未生成预期安装器：$taskInstaller"
}
Write-Output $taskInstaller
