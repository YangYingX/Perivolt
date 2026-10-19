param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { & (Join-Path $PSScriptRoot 'Bootstrap.ps1') }
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$taskProject = Join-Path $taskRoot 'PowerHub.csproj'
$taskConfiguration = if ($Publish) { 'Release' } else { 'Debug' }
& $taskDotnet build $taskProject -c $taskConfiguration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$taskExtensions = Get-ChildItem -LiteralPath (Join-Path $taskRoot 'Extensions') -Filter '*.csproj' -Recurse
foreach ($taskExtension in $taskExtensions) {
    & $taskDotnet build $taskExtension.FullName -c $taskConfiguration -p:BuildProjectReferences=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Publish) {
    & $taskDotnet publish $taskProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -o (Join-Path $taskRoot 'dist\PowerHub')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$taskOutput = if ($Publish) { Join-Path $taskRoot 'dist\PowerHub' } else { Join-Path $taskRoot 'bin\Debug\net10.0-windows10.0.19041.0' }
foreach ($taskExtension in $taskExtensions) {
    $taskManifests = Get-ChildItem -LiteralPath $taskExtension.DirectoryName -Filter '*.json' -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and ($_.Name -eq 'plugin.json' -or $_.Name.EndsWith('.plugin.json')) }
    foreach ($taskManifest in $taskManifests) {
        $taskMetadata = Get-Content -LiteralPath $taskManifest.FullName -Raw | ConvertFrom-Json
        if ($taskMetadata.Id -notmatch '^[a-z0-9][a-z0-9-]*$' -or [System.IO.Path]::GetFileName($taskMetadata.Assembly) -ne $taskMetadata.Assembly) { throw 'Invalid bundled plugin manifest' }
        $taskDestination = Join-Path $taskOutput ('plugins\' + $taskMetadata.Id)
        New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
        Copy-Item -LiteralPath $taskManifest.FullName -Destination (Join-Path $taskDestination 'plugin.json') -Force
        $taskAssembly = Join-Path $taskExtension.DirectoryName ('bin\' + $taskConfiguration + '\net10.0-windows10.0.19041.0\' + $taskMetadata.Assembly)
        Copy-Item -LiteralPath $taskAssembly -Destination $taskDestination -Force
    }
}
if ($Publish) {
    $taskNotices = Join-Path $taskOutput 'runtime-notices'
    New-Item -ItemType Directory -Path $taskNotices -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot '.tools\dotnet\LICENSE.txt') -Destination (Join-Path $taskNotices 'DOTNET-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $taskRoot '.tools\dotnet\ThirdPartyNotices.txt') -Destination (Join-Path $taskNotices 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
}
exit 0
