<#
.SYNOPSIS
构建并打包一个独立可选插件，不修改主程序发布目录。
.EXAMPLE
.\scripts\Build-Plugin.ps1 -Project Extensions\CharacterThemes\CharacterThemes.csproj
#>
param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Project)

$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Assert-ChildPath([string]$Path, [string]$Root) {
    $taskFullPath = [System.IO.Path]::GetFullPath($Path)
    $taskAllowedRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (-not $taskFullPath.StartsWith($taskAllowedRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "路径必须位于 $taskAllowedRoot 内：$taskFullPath"
    }
    for ($taskAncestor = $taskFullPath; $taskAncestor -ne $taskAllowedRoot; $taskAncestor = Split-Path -Parent $taskAncestor) {
        if ((Test-Path -LiteralPath $taskAncestor) -and
            ((Get-Item -LiteralPath $taskAncestor -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "不支持符号链接或目录联接：$taskAncestor"
        }
    }
    return $taskFullPath
}

$taskProjectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $taskRoot $Project }
$taskProject = Assert-ChildPath $taskProjectPath $taskRoot
$taskProject = Assert-ChildPath $taskProject (Join-Path $taskRoot 'Extensions')
if ([System.IO.Path]::GetExtension($taskProject) -ne '.csproj' -or -not (Test-Path -LiteralPath $taskProject -PathType Leaf)) {
    throw 'Project 必须指向仓库 Extensions 目录内现有的 .csproj 文件。'
}
$taskPluginRoot = Split-Path -Parent $taskProject
$taskManifest = Assert-ChildPath (Join-Path $taskPluginRoot 'plugin.json') $taskRoot
if (-not (Test-Path -LiteralPath $taskManifest -PathType Leaf)) { throw "插件根目录缺少 plugin.json：$taskPluginRoot" }
$taskMetadata = Get-Content -LiteralPath $taskManifest -Raw | ConvertFrom-Json
if ($taskMetadata.Id -cnotmatch '^[a-z0-9][a-z0-9-]*$' -or
    $taskMetadata.Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$' -or
    $taskMetadata.Assembly -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*\.dll$' -or
    $taskMetadata.Assembly -eq 'PowerHub.dll') {
    throw 'plugin.json 的 Id、Version 或 Assembly 无效；不能将主程序契约程序集作为插件打包。'
}
$taskNotices = @('LICENSE', 'NOTICE.md', 'AUTHORS.md', 'LICENSES\MIT-legacy.txt', 'THIRD-PARTY-NOTICES.md')
foreach ($taskNotice in $taskNotices) {
    $taskNoticePath = Assert-ChildPath (Join-Path $taskRoot $taskNotice) $taskRoot
    if (-not (Test-Path -LiteralPath $taskNoticePath -PathType Leaf)) { throw "仓库缺少声明文件：$taskNotice" }
}
$taskReadme = Assert-ChildPath (Join-Path $taskPluginRoot 'README.md') $taskRoot
$taskOutput = Assert-ChildPath (Join-Path $taskRoot 'artifacts\plugins') $taskRoot
$taskBaseName = "Perivolt-Plugin-$($taskMetadata.Id)-$($taskMetadata.Version)"
$taskZip = Assert-ChildPath (Join-Path $taskOutput ($taskBaseName + '.zip')) $taskRoot
$taskChecksum = Assert-ChildPath ($taskZip + '.sha256') $taskRoot
foreach ($taskDestination in @($taskZip, $taskChecksum)) {
    if (Test-Path -LiteralPath $taskDestination -PathType Container) { throw "输出目标必须为普通文件：$taskDestination" }
}

$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet -PathType Leaf)) {
    $taskInstalledDotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $taskInstalledDotnet) { throw '未找到 .NET SDK。请先安装项目所需的 SDK，再运行此脚本。' }
    $taskDotnet = $taskInstalledDotnet.Source
}
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\cli'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'

& $taskDotnet build (Join-Path $taskRoot 'PowerHub.csproj') -c Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "主程序契约构建失败，退出码 $LASTEXITCODE。" }
& $taskDotnet build $taskProject -c Release -p:BuildProjectReferences=false -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "插件构建失败，退出码 $LASTEXITCODE。" }
$taskTargetPath = & $taskDotnet msbuild $taskProject -p:Configuration=Release -p:BuildProjectReferences=false -getProperty:TargetPath
if ($LASTEXITCODE -ne 0) { throw "读取插件输出路径失败，退出码 $LASTEXITCODE。" }
$taskAssembly = Assert-ChildPath ([string]$taskTargetPath).Trim() $taskPluginRoot
if (-not (Test-Path -LiteralPath $taskAssembly -PathType Leaf) -or
    [System.IO.Path]::GetFileName($taskAssembly) -ne $taskMetadata.Assembly) {
    throw "插件输出与清单 Assembly 不一致：$taskAssembly"
}
$taskAssemblyVersion = (Get-Item -LiteralPath $taskAssembly).VersionInfo.ProductVersion
if ($taskAssemblyVersion -ne $taskMetadata.Version -and -not $taskAssemblyVersion.StartsWith($taskMetadata.Version + '+')) {
    throw "插件程序集版本 $taskAssemblyVersion 与清单版本 $($taskMetadata.Version) 不一致。"
}

New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskStage = Assert-ChildPath (Join-Path $taskOutput ('.staging-' + [Guid]::NewGuid().ToString('N'))) $taskRoot
try {
    $taskPayload = Join-Path $taskStage $taskMetadata.Id
    New-Item -ItemType Directory -Path (Join-Path $taskPayload 'LICENSES') -Force | Out-Null
    Copy-Item -LiteralPath $taskManifest -Destination (Join-Path $taskPayload 'plugin.json')
    Copy-Item -LiteralPath $taskAssembly -Destination $taskPayload
    if (Test-Path -LiteralPath $taskReadme -PathType Leaf) { Copy-Item -LiteralPath $taskReadme -Destination $taskPayload }
    foreach ($taskNotice in $taskNotices) {
        Copy-Item -LiteralPath (Join-Path $taskRoot $taskNotice) -Destination (Join-Path $taskPayload $taskNotice)
    }
    $taskStagedZip = Join-Path $taskStage ($taskBaseName + '.zip')
    Compress-Archive -LiteralPath $taskPayload -DestinationPath $taskStagedZip -CompressionLevel Optimal
    $taskDigest = (Get-FileHash -LiteralPath $taskStagedZip -Algorithm SHA256).Hash.ToLowerInvariant()
    ($taskDigest + '  ' + [System.IO.Path]::GetFileName($taskZip)) |
        Set-Content -LiteralPath ($taskStagedZip + '.sha256') -Encoding ascii
    Move-Item -LiteralPath $taskStagedZip -Destination $taskZip -Force
    Move-Item -LiteralPath ($taskStagedZip + '.sha256') -Destination $taskChecksum -Force
}
finally {
    $taskStage = Assert-ChildPath $taskStage $taskOutput
    if (Test-Path -LiteralPath $taskStage) { Remove-Item -LiteralPath $taskStage -Recurse -Force }
}
Write-Output $taskZip
Write-Output $taskChecksum
