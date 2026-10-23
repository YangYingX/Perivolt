param([ValidateSet('osx-arm64', 'osx-x64')][string]$Runtime = 'osx-arm64')
$ErrorActionPreference = 'Stop'

function Assert-ControlledOutput([string]$Path) {
    $taskResolved = [IO.Path]::GetFullPath($Path)
    if (-not $taskResolved.StartsWith($script:taskOutputPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected output path: $taskResolved" }
    $taskAncestor = $taskResolved
    while ($taskAncestor.Length -ge $script:taskWorkspace.Length) {
        if ((Test-Path -LiteralPath $taskAncestor) -and
            ((Get-Item -LiteralPath $taskAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Output cannot contain directory links: $taskAncestor" }
        $taskAncestor = Split-Path -Parent $taskAncestor
        if (-not $taskAncestor) { break }
    }
}

function Write-BigEndian32([IO.Stream]$Stream, [int]$Value) {
    $taskBytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($taskBytes) }
    $Stream.Write($taskBytes, 0, $taskBytes.Length)
}

function Write-Icns([string]$Assets, [string]$Destination) {
    Assert-ControlledOutput $Destination
    $taskChunks = @(
        @{ Size = 16; Type = 'icp4' }, @{ Size = 32; Type = 'icp5' }, @{ Size = 64; Type = 'icp6' },
        @{ Size = 128; Type = 'ic07' }, @{ Size = 256; Type = 'ic08' }
    )
    $taskTotal = 8
    foreach ($taskChunk in $taskChunks) {
        $taskChunk.Bytes = [IO.File]::ReadAllBytes((Join-Path $Assets ("powerhub-{0}.png" -f $taskChunk.Size)))
        if ($taskChunk.Bytes.Length -lt 24 -or [BitConverter]::ToString($taskChunk.Bytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') { throw 'Invalid PNG icon source' }
        $taskTotal += 8 + $taskChunk.Bytes.Length
    }
    $taskStream = [IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $taskMagic = [Text.Encoding]::ASCII.GetBytes('icns'); $taskStream.Write($taskMagic, 0, 4)
        Write-BigEndian32 $taskStream $taskTotal
        foreach ($taskChunk in $taskChunks) {
            $taskType = [Text.Encoding]::ASCII.GetBytes($taskChunk.Type); $taskStream.Write($taskType, 0, 4)
            Write-BigEndian32 $taskStream (8 + $taskChunk.Bytes.Length)
            $taskStream.Write($taskChunk.Bytes, 0, $taskChunk.Bytes.Length)
        }
    }
    finally { $taskStream.Dispose() }
}

function Set-UnixZipCreator([string]$Path) {
    Assert-ControlledOutput $Path
    $taskStream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $taskReader = [IO.BinaryReader]::new($taskStream, [Text.Encoding]::UTF8, $true)
    try {
        if ($taskStream.Length -lt 22 -or $taskStream.Length -ge [uint32]::MaxValue) { throw 'ZIP must be smaller than 4 GiB; ZIP64 is unsupported' }
        $taskTailLength = [int][Math]::Min(65557, $taskStream.Length)
        $taskStream.Position = $taskStream.Length - $taskTailLength
        $taskTail = $taskReader.ReadBytes($taskTailLength)
        $taskEnd = -1
        for ($taskIndex = $taskTail.Length - 22; $taskIndex -ge 0; $taskIndex--) {
            if ([BitConverter]::ToUInt32($taskTail, $taskIndex) -eq 0x06054b50 -and
                $taskIndex + 22 + [BitConverter]::ToUInt16($taskTail, $taskIndex + 20) -eq $taskTail.Length) { $taskEnd = $taskIndex; break }
        }
        if ($taskEnd -lt 0) { throw 'ZIP end record is missing' }
        $taskCount = [BitConverter]::ToUInt16($taskTail, $taskEnd + 10)
        $taskCentralSize = [BitConverter]::ToUInt32($taskTail, $taskEnd + 12)
        $taskCentralOffset = [BitConverter]::ToUInt32($taskTail, $taskEnd + 16)
        $taskEndOffset = $taskStream.Length - $taskTailLength + $taskEnd
        if ([BitConverter]::ToUInt16($taskTail, $taskEnd + 4) -ne 0 -or [BitConverter]::ToUInt16($taskTail, $taskEnd + 6) -ne 0 -or
            [BitConverter]::ToUInt16($taskTail, $taskEnd + 8) -ne $taskCount -or $taskCount -eq [uint16]::MaxValue -or
            $taskCentralSize -eq [uint32]::MaxValue -or $taskCentralOffset -eq [uint32]::MaxValue -or
            [long]$taskCentralOffset + $taskCentralSize -ne $taskEndOffset) { throw 'Unsupported multipart or ZIP64 archive' }
        $taskStream.Position = $taskCentralOffset
        for ($taskIndex = 0; $taskIndex -lt $taskCount; $taskIndex++) {
            $taskPosition = $taskStream.Position
            $taskHeader = $taskReader.ReadBytes(46)
            if ($taskHeader.Length -ne 46 -or [BitConverter]::ToUInt32($taskHeader, 0) -ne 0x02014b50) { throw 'Invalid ZIP central directory' }
            if ([BitConverter]::ToUInt32($taskHeader, 20) -eq [uint32]::MaxValue -or [BitConverter]::ToUInt32($taskHeader, 24) -eq [uint32]::MaxValue -or
                [BitConverter]::ToUInt32($taskHeader, 42) -eq [uint32]::MaxValue -or [BitConverter]::ToUInt16($taskHeader, 34) -ne 0) { throw 'Unsupported ZIP64 entry' }
            $taskNext = $taskPosition + 46 + [BitConverter]::ToUInt16($taskHeader, 28) + [BitConverter]::ToUInt16($taskHeader, 30) + [BitConverter]::ToUInt16($taskHeader, 32)
            if ($taskNext -gt $taskEndOffset) { throw 'ZIP central directory entry exceeds its bounds' }
            $taskStream.Position = $taskPosition + 5
            $taskStream.WriteByte(3) # Unix creator, so extractors honor the stored POSIX file mode.
            $taskStream.Position = $taskNext
        }
        if ($taskStream.Position -ne $taskEndOffset) { throw 'ZIP central directory length mismatch' }
        $taskStream.Flush($true)
    }
    finally { $taskReader.Dispose(); $taskStream.Dispose() }
}

function New-MacZip([string]$Bundle, [string]$Destination) {
    Assert-ControlledOutput $Bundle
    Assert-ControlledOutput $Destination
    $taskItems = @(Get-Item -LiteralPath $Bundle) + @(Get-ChildItem -LiteralPath $Bundle -Recurse -Force)
    if ($taskItems.Count -ge [uint16]::MaxValue -or ($taskItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })) { throw 'Too many bundle entries or unsupported directory links' }
    $taskInputBytes = [long]0
    foreach ($taskItem in $taskItems) { if (-not $taskItem.PSIsContainer) { $taskInputBytes += $taskItem.Length } }
    if ($taskInputBytes + [long]$taskItems.Count * 131072 -ge [uint32]::MaxValue) { throw 'Bundle is too large for a ZIP without ZIP64' }
    $taskTemporary = $Destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    Assert-ControlledOutput $taskTemporary
    try {
        $taskStream = [IO.File]::Open($taskTemporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $taskArchive = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Create, $true, [Text.Encoding]::UTF8)
        try {
            $taskParent = Split-Path -Parent $Bundle
            foreach ($taskItem in $taskItems | Sort-Object FullName) {
                $taskName = [IO.Path]::GetRelativePath($taskParent, $taskItem.FullName).Replace('\', '/')
                if ($taskItem.PSIsContainer) { $taskName += '/' }
                if ([Text.Encoding]::UTF8.GetByteCount($taskName) -gt [uint16]::MaxValue) { throw 'ZIP entry name is too long' }
                $taskEntry = $taskArchive.CreateEntry($taskName, [IO.Compression.CompressionLevel]::Optimal)
                $taskMode = if ($taskItem.PSIsContainer) { 0x41ed } elseif ($taskItem.Name -in @('Perivolt', 'createdump') -or $taskItem.Extension -eq '.dylib') { 0x81ed } else { 0x81a4 }
                $taskEntry.ExternalAttributes = ($taskMode -shl 16) -bor $(if ($taskItem.PSIsContainer) { 16 } else { 0 })
                if (-not $taskItem.PSIsContainer) {
                    $taskSource = [IO.File]::OpenRead($taskItem.FullName)
                    $taskTarget = $taskEntry.Open()
                    try { $taskSource.CopyTo($taskTarget) }
                    finally { $taskTarget.Dispose(); $taskSource.Dispose() }
                }
            }
        }
        finally { $taskArchive.Dispose(); $taskStream.Dispose() }
        Set-UnixZipCreator $taskTemporary
        [IO.File]::Move($taskTemporary, $Destination, $true)
    }
    finally { if (Test-Path -LiteralPath $taskTemporary) { Remove-Item -LiteralPath $taskTemporary -Force } }
}

$taskWorkspace = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$taskOutputRoot = Join-Path $taskWorkspace 'artifacts/macOS-preview'
$taskOutputPrefix = [IO.Path]::GetFullPath($taskOutputRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$taskRidRoot = Join-Path $taskOutputRoot $Runtime
$taskBundle = Join-Path $taskRidRoot 'Perivolt.app'
$taskMacOs = Join-Path $taskBundle 'Contents/MacOS'
$taskResources = Join-Path $taskBundle 'Contents/Resources'
Assert-ControlledOutput $taskBundle
$taskProject = Join-Path $taskWorkspace 'macOS/Perivolt.Mac.csproj'
[xml]$taskProjectXml = Get-Content -LiteralPath $taskProject -Raw -Encoding UTF8
$taskVersion = [string](@($taskProjectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0])
if ($taskVersion -notmatch '^(\d+\.\d+\.\d+)(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid macOS project version' }
$taskShortVersion = $Matches[1]
if ([string]$taskProjectXml.Project.PropertyGroup.AssemblyName -ne 'Perivolt') { throw 'The macOS apphost must be named Perivolt' }
$taskDotnet = Join-Path $taskWorkspace '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$taskDotnetTarget = [IO.File]::ResolveLinkTarget($taskDotnet, $true)
if ($null -ne $taskDotnetTarget) { $taskDotnet = $taskDotnetTarget.FullName }
$taskRuntimeLicense = Join-Path (Split-Path -Parent $taskDotnet) 'LICENSE.txt'
$taskRuntimeNotices = Join-Path (Split-Path -Parent $taskDotnet) 'ThirdPartyNotices.txt'
$taskMacNotices = Join-Path $taskWorkspace 'macOS/THIRD-PARTY-NOTICES.macOS.md'
if (-not (Test-Path -LiteralPath $taskMacNotices -PathType Leaf)) { throw "Missing required notice: $taskMacNotices" }
if (Test-Path -LiteralPath $taskBundle) {
    if (Get-ChildItem -LiteralPath $taskBundle -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Existing bundle cannot contain directory links' }
    # Only replace this RID's generated bundle; other previews, archives and application data remain intact.
    Remove-Item -LiteralPath $taskBundle -Recurse -Force
}
New-Item -ItemType Directory -Path $taskMacOs, $taskResources -Force | Out-Null
$env:DOTNET_CLI_HOME = Join-Path $taskWorkspace '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $taskWorkspace '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
& $taskDotnet publish $taskProject -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $taskMacOs
if ($LASTEXITCODE -ne 0) { throw "macOS publish failed ($LASTEXITCODE)" }
$taskAppHost = Join-Path $taskMacOs 'Perivolt'
$taskHostBytes = [IO.File]::ReadAllBytes($taskAppHost)
$taskCpu = if ($Runtime -eq 'osx-arm64') { 0x0100000c } else { 0x01000007 }
if ($taskHostBytes.Length -lt 32 -or [BitConverter]::ToUInt32($taskHostBytes, 0) -ne 0xfeedfacfL -or [BitConverter]::ToUInt32($taskHostBytes, 4) -ne $taskCpu) { throw 'Published apphost is not the expected Mach-O architecture' }
$taskDeps = Get-Content -LiteralPath (Join-Path $taskMacOs 'Perivolt.deps.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$taskRuntimeLibraries = @($taskDeps.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.$Runtime/", [StringComparison]::OrdinalIgnoreCase) })
if ($taskRuntimeLibraries.Count -eq 1) {
    $taskRuntimeVersion = $taskRuntimeLibraries[0].Split('/')[1]
    if ($taskRuntimeVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid published runtime version' }
    $taskRuntimePackage = Join-Path $env:NUGET_PACKAGES ("microsoft.netcore.app.runtime.$Runtime/$taskRuntimeVersion")
    $taskPackageLicense = Join-Path $taskRuntimePackage 'LICENSE.TXT'
    $taskPackageNotices = Join-Path $taskRuntimePackage 'THIRD-PARTY-NOTICES.TXT'
    if ((Test-Path -LiteralPath $taskPackageLicense -PathType Leaf) -and (Test-Path -LiteralPath $taskPackageNotices -PathType Leaf)) {
        $taskRuntimeLicense = $taskPackageLicense; $taskRuntimeNotices = $taskPackageNotices
    }
}
foreach ($taskRequired in @($taskRuntimeLicense, $taskRuntimeNotices)) {
    if (-not (Test-Path -LiteralPath $taskRequired -PathType Leaf)) { throw "Missing required runtime notice: $taskRequired" }
}

$taskPlist = [Xml.XmlDocument]::new(); $taskPlist.XmlResolver = $null
$taskPlist.Load((Join-Path $taskWorkspace 'macOS/Info.plist'))
foreach ($taskField in @{ CFBundleShortVersionString = $taskShortVersion; CFBundleVersion = $taskShortVersion; CFBundleGetInfoString = "电栖 · Perivolt $taskVersion" }.GetEnumerator()) {
    $taskValue = $taskPlist.SelectSingleNode("/plist/dict/key[text()='$($taskField.Key)']/following-sibling::*[1]")
    if ($null -eq $taskValue -or $taskValue.Name -ne 'string') { throw 'Invalid Info.plist version fields' }
    $taskValue.InnerText = $taskField.Value
}
$taskXmlSettings = [Xml.XmlWriterSettings]::new(); $taskXmlSettings.Indent = $true; $taskXmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
$taskXmlWriter = [Xml.XmlWriter]::Create((Join-Path $taskBundle 'Contents/Info.plist'), $taskXmlSettings)
try {
    $taskXmlWriter.WriteStartDocument()
    # XmlDocument.Save emits an empty DTD subset ([]) that Apple's plutil rejects.
    # PowerShell's $null becomes an empty string here; pass a real null string.
    $taskXmlWriter.WriteDocType('plist', $taskPlist.DocumentType.PublicId, $taskPlist.DocumentType.SystemId, [System.Management.Automation.Language.NullString]::Value)
    $taskPlist.DocumentElement.WriteTo($taskXmlWriter)
    $taskXmlWriter.WriteEndDocument()
}
finally { $taskXmlWriter.Dispose() }
Write-Icns (Join-Path $taskWorkspace 'Assets') (Join-Path $taskResources 'Perivolt.icns')
foreach ($taskNotice in @('LICENSE', 'NOTICE.md', 'AUTHORS.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $taskWorkspace $taskNotice) -Destination (Join-Path $taskResources $taskNotice) -Force
}
Copy-Item -LiteralPath (Join-Path $taskWorkspace 'macOS/THIRD-PARTY-NOTICES.macOS.md') -Destination $taskResources -Force
$taskLicenses = Join-Path $taskResources 'LICENSES'; New-Item -ItemType Directory -Path $taskLicenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskWorkspace 'LICENSES/MIT-legacy.txt') -Destination $taskLicenses -Force
Get-ChildItem -LiteralPath (Join-Path $taskWorkspace 'macOS/LICENSES') -File | Copy-Item -Destination $taskLicenses -Force
$taskNotices = Join-Path $taskResources 'runtime-notices'; New-Item -ItemType Directory -Path $taskNotices -Force | Out-Null
Copy-Item -LiteralPath $taskRuntimeLicense -Destination (Join-Path $taskNotices 'DOTNET-LICENSE.txt') -Force
Copy-Item -LiteralPath $taskRuntimeNotices -Destination (Join-Path $taskNotices 'DOTNET-THIRD-PARTY-NOTICES.txt') -Force
$taskZip = Join-Path $taskRidRoot ("Perivolt-$taskVersion-$Runtime.zip")
New-MacZip $taskBundle $taskZip
$taskHash = (Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-ControlledOutput ($taskZip + '.sha256')
[IO.File]::WriteAllText($taskZip + '.sha256', "$taskHash  $([IO.Path]::GetFileName($taskZip))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Created $taskZip"
Write-Output 'Unsigned development preview; no signing, notarization or release upload was performed.'
