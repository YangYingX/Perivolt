param([switch]$Publish, [string]$DiagnosticUploadEndpoint = '')
$ErrorActionPreference = 'Stop'
$taskBuildProperties = @()
if ($DiagnosticUploadEndpoint) {
    $taskEndpointUri = $null
    if (-not [Uri]::TryCreate($DiagnosticUploadEndpoint, [UriKind]::Absolute, [ref]$taskEndpointUri) -or
        $taskEndpointUri.Scheme -ne 'https' -or $taskEndpointUri.UserInfo -or $taskEndpointUri.Query -or
        $taskEndpointUri.Fragment -or $taskEndpointUri.AbsolutePath -ne '/v1/reports') {
        throw '诊断接口必须为无凭据、查询参数和片段的 HTTPS /v1/reports 地址。'
    }
    $taskBuildProperties += "-p:DiagnosticUploadEndpoint=$($taskEndpointUri.AbsoluteUri)"
}
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
$taskOutput = if ($Publish) { Join-Path $taskRoot 'dist\PowerHub' } else { Join-Path $taskRoot 'bin\Debug\net10.0-windows10.0.19041.0' }
$taskRepositoryPrefix = [System.IO.Path]::GetFullPath($taskRoot).TrimEnd('\') + '\'
$taskPluginOutput = [System.IO.Path]::GetFullPath((Join-Path $taskOutput 'plugins'))
if (-not $taskPluginOutput.StartsWith($taskRepositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid bundled plugin output path' }
$taskOutputAncestor = $taskPluginOutput
while ($taskOutputAncestor.StartsWith($taskRepositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    if ((Test-Path -LiteralPath $taskOutputAncestor) -and
        ((Get-Item -LiteralPath $taskOutputAncestor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) { throw 'Build output cannot contain directory links' }
    $taskOutputAncestor = Split-Path -Parent $taskOutputAncestor
}
$taskExtensions = @(
    @{ Project = 'Essentials\Essentials.csproj'; Plugins = @(
        @{ Id = 'aurora-theme'; Manifest = 'manifests\aurora-theme\plugin.json' },
        @{ Id = 'full-charge'; Manifest = 'manifests\full-charge\plugin.json' },
        @{ Id = 'quiet-hours'; Manifest = 'manifests\quiet-hours\plugin.json' }
    ) },
    @{ Project = 'VendorProtocols\VendorProtocols.csproj'; Plugins = @(
        @{ Id = 'vendor-protocols'; Manifest = 'plugin.json' }
    ) }
)
& $taskDotnet build $taskProject -c $taskConfiguration @taskBuildProperties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($taskExtension in $taskExtensions) {
    $taskExtensionProject = Join-Path (Join-Path $taskRoot 'Extensions') $taskExtension.Project
    & $taskDotnet build $taskExtensionProject -c $taskConfiguration -p:BuildProjectReferences=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Publish) {
    & $taskDotnet publish $taskProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false @taskBuildProperties -o (Join-Path $taskRoot 'dist\PowerHub')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (Test-Path -LiteralPath $taskPluginOutput) {
    if (@(Get-ChildItem -LiteralPath $taskPluginOutput -Recurse -Force |
        Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint }).Count) { throw 'Bundled plugin output cannot contain directory links' }
    Remove-Item -LiteralPath $taskPluginOutput -Recurse -Force
}
foreach ($taskExtension in $taskExtensions) {
    $taskExtensionDirectory = Split-Path -Parent (Join-Path (Join-Path $taskRoot 'Extensions') $taskExtension.Project)
    foreach ($taskPlugin in $taskExtension.Plugins) {
        $taskManifest = Join-Path $taskExtensionDirectory $taskPlugin.Manifest
        $taskMetadata = Get-Content -LiteralPath $taskManifest -Raw | ConvertFrom-Json
        if ($taskMetadata.Id -cne $taskPlugin.Id -or [System.IO.Path]::GetFileName($taskMetadata.Assembly) -ne $taskMetadata.Assembly) { throw 'Invalid bundled plugin manifest' }
        $taskDestination = Join-Path $taskOutput ('plugins\' + $taskMetadata.Id)
        New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
        Copy-Item -LiteralPath $taskManifest -Destination (Join-Path $taskDestination 'plugin.json') -Force
        $taskAssembly = Join-Path $taskExtensionDirectory ('bin\' + $taskConfiguration + '\net10.0-windows10.0.19041.0\' + $taskMetadata.Assembly)
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
