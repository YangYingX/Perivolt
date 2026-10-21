param([ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')][string]$Version = '1.0.7')

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCompiler = Join-Path $taskRoot '.tools\inno\compiler\ISCC.exe'
$taskPayload = Join-Path $taskRoot 'dist\PowerHub'
$taskOutput = Join-Path $taskRoot 'dist'
$taskScript = Join-Path $taskRoot 'installer\PowerHub.iss'

if (-not (Test-Path -LiteralPath $taskCompiler -PathType Leaf)) {
    throw "未找到 Inno Setup 编译器：$taskCompiler"
}
foreach ($required in @('Perivolt.exe', 'THIRD-PARTY-NOTICES.md', 'LICENSE', 'NOTICE.md', 'AUTHORS.md', 'LICENSES\MIT-legacy.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskPayload $required) -PathType Leaf)) {
        throw "发布目录缺少 $required。请先运行 scripts\Build.ps1 -Publish。"
    }
}
foreach ($notice in @('THIRD-PARTY-NOTICES.md', 'LICENSE', 'NOTICE.md', 'AUTHORS.md', 'LICENSES\MIT-legacy.txt')) {
    if ((Get-FileHash -LiteralPath (Join-Path $taskPayload $notice) -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $taskRoot $notice) -Algorithm SHA256).Hash) {
        throw "发布目录的 $notice 与源码不一致。请先重新运行 scripts\Build.ps1 -Publish。"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $taskRoot 'Assets\powerhub.ico') -PathType Leaf)) {
    throw '缺少 Assets\powerhub.ico。请先生成应用图标。'
}

$taskBaseName = "Perivolt-Setup-$Version-x64"
$taskFileVersion = $Version.Split('-')[0]
$taskPayloadVersion = (Get-Item -LiteralPath (Join-Path $taskPayload 'Perivolt.exe')).VersionInfo.ProductVersion
if ($taskPayloadVersion -ne $Version -and -not $taskPayloadVersion.StartsWith($Version + '+')) { throw "安装器版本 $Version 与程序版本 $taskPayloadVersion 不一致。" }
& $taskCompiler "/DAppVersion=$Version" "/DAppFileVersion=$taskFileVersion" "/O$taskOutput" "/F$taskBaseName" $taskScript
if ($LASTEXITCODE -ne 0) { throw "安装器编译失败，退出码 $LASTEXITCODE。" }

$taskInstaller = Join-Path $taskOutput "$taskBaseName.exe"
if (-not (Test-Path -LiteralPath $taskInstaller -PathType Leaf)) {
    throw "编译器未生成预期安装器：$taskInstaller"
}
$taskDigest = (Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
($taskDigest + '  ' + [System.IO.Path]::GetFileName($taskInstaller)) |
    Set-Content -LiteralPath ($taskInstaller + '.sha256') -Encoding ascii
Write-Output $taskInstaller
