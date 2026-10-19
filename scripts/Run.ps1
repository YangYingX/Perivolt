param([switch]$Scan, [string]$Output)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$taskArguments = @((Join-Path $taskRoot 'bin\Debug\net10.0-windows10.0.19041.0\PowerHub.dll'))
if ($Scan) { $taskArguments += '--scan' }
if ($Output) { $taskArguments += @('--output', $Output) }
& (Join-Path $taskRoot '.tools\dotnet\dotnet.exe') @taskArguments
exit $LASTEXITCODE
