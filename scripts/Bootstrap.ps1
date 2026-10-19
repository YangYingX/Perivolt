param()
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnetRoot = Join-Path $taskRoot '.tools\dotnet'
$taskDotnet = Join-Path $taskDotnetRoot 'dotnet.exe'
if (Test-Path -LiteralPath $taskDotnet) {
    & $taskDotnet --version
    exit $LASTEXITCODE
}
$taskMetadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$taskRelease = $taskMetadata.releases | Where-Object { $_.sdk.version -eq $taskMetadata.'latest-sdk' } | Select-Object -First 1
$taskArchive = $taskRelease.sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (-not $taskArchive -or -not $taskArchive.url.StartsWith('https://builds.dotnet.microsoft.com/')) {
    throw 'Microsoft SDK metadata did not contain the expected Windows x64 archive.'
}
New-Item -ItemType Directory -Path $taskDotnetRoot -Force | Out-Null
$taskZip = Join-Path $taskRoot '.tools\dotnet-sdk.zip'
Write-Host "Downloading Microsoft .NET SDK $($taskRelease.sdk.version) into the project..."
Invoke-WebRequest -Uri $taskArchive.url -OutFile $taskZip
$taskHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA512).Hash
if ($taskHash -ne $taskArchive.hash) { throw 'SDK SHA512 verification failed.' }
Expand-Archive -LiteralPath $taskZip -DestinationPath $taskDotnetRoot -Force
Remove-Item -LiteralPath $taskZip
& $taskDotnet --version
exit $LASTEXITCODE
