# One-time migration of the pre-retention Output layout. Future rotation is handled by FirmwareOutputStore.
[CmdletBinding()]
param([switch] $Apply, [switch] $Resume)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$outputRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot 'Output')).Path
$recordsRoot = Join-Path $PSScriptRoot 'Previous Output'
$culture = [Globalization.CultureInfo]::InvariantCulture

function Assert-Within([string] $Path, [string] $Root) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the intended directory: $full"
    }
    if ((Test-Path -LiteralPath $full) -and (Get-Item -LiteralPath $full).LinkType) {
        throw "Refusing linked path: $full"
    }
}

if (-not $Resume -and (Test-Path -LiteralPath (Join-Path $outputRoot 'ShinyGo60.uf2'))) { throw 'Output is already organized.' }
$originalEntries = @(Get-ChildItem -LiteralPath $outputRoot -Force | Where-Object {
    -not $Resume -or ($_.Name -notin @('ShinyGo60.uf2','layout-manifest.json','build.log','firmware-build.json') -and $_.Name -notlike 'Firmware-*')
})
$builds = @(foreach ($directory in $originalEntries | Where-Object { $_.PSIsContainer -and $_.Name -match '^ShinyGo60-\d{8}-\d{6}-[a-f0-9]+' }) {
    Assert-Within $directory.FullName $outputRoot
    $files = @(Get-ChildItem -LiteralPath $directory.FullName -Force)
    $uf2 = @($files | Where-Object Extension -eq '.uf2')
    if ($uf2.Count -ne 1) { throw "Unexpected build contents: $directory" }
    foreach ($file in $files) {
        Assert-Within $file.FullName $outputRoot
        if ($file.Name -notin @($uf2[0].Name, 'build.log', 'layout-manifest.json', 'SUPERSEDED.txt', 'WindowsArtifacts')) { throw "Unexpected file: $file" }
    }
    $log = Get-Content -LiteralPath (Join-Path $directory.FullName 'build.log') -Raw
    $started = [regex]::Match($log, '(?m)^StartedUtc: (.+)\r?$').Groups[1].Value.Trim()
    $duration = [regex]::Matches($log, '(?m)^Duration: ([0-9.]+) seconds\r?$')
    if (-not $started -or $duration.Count -eq 0 -or $log -notmatch 'Status: succeeded') { throw "Incomplete build log: $directory" }
    $created = [datetimeoffset]::Parse($started, $culture).AddSeconds([double]::Parse($duration[-1].Groups[1].Value, $culture)).ToLocalTime()
    $hash = (Get-FileHash -LiteralPath $uf2[0].FullName).Hash.ToLowerInvariant()
    if ($log -notmatch "Uf2Sha256: $hash") { throw "Firmware checksum mismatch: $directory" }
    [pscustomobject]@{ Directory=$directory; Uf2=$uf2[0]; Created=$created; Hash=$hash; Superseded=(Test-Path -LiteralPath (Join-Path $directory.FullName 'SUPERSEDED.txt')) }
})
$retained = @($builds | Where-Object { -not $_.Superseded } | Sort-Object Created -Descending | Select-Object -First 5)
if ($retained.Count -ne 5) { throw 'Expected at least five complete existing builds.' }
$retained | Select-Object @{n='Build';e={$_.Directory.Name}},Created,Hash
if (-not $Apply) { return }

# Prepare and verify all retained copies before retiring any original build.
for ($index=0; -not $Resume -and $index -lt $retained.Count; $index++) {
    $build = $retained[$index]
    $destination = if ($index -eq 0) { $outputRoot } else { Join-Path $outputRoot ('Firmware-' + $build.Created.ToString('yyyy-MM-dd_HH-mm-ss-fff', $culture)) }
    if ($index -gt 0) {
        Assert-Within $destination $outputRoot
        if (Test-Path -LiteralPath $destination) { throw "Archive already exists: $destination" }
        New-Item -ItemType Directory -Path $destination | Out-Null
    }
    Copy-Item -LiteralPath $build.Uf2.FullName -Destination (Join-Path $destination 'ShinyGo60.uf2')
    foreach ($name in @('layout-manifest.json', 'build.log')) {
        $target = Join-Path $destination $name
        if (Test-Path -LiteralPath $target) { throw "Output file already exists: $target" }
        Copy-Item -LiteralPath (Join-Path $build.Directory.FullName $name) -Destination $target
    }
    @{SchemaVersion=1;CreatedAt=$build.Created.ToString('O');BuildId=[guid]::NewGuid().ToString('N')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'firmware-build.json') -Encoding utf8
    if ((Get-FileHash -LiteralPath (Join-Path $destination 'ShinyGo60.uf2')).Hash.ToLowerInvariant() -ne $build.Hash) { throw 'Copied firmware verification failed.' }
}

New-Item -ItemType Directory -Path $recordsRoot -Force | Out-Null
foreach ($build in $builds) {
    $destination = Join-Path $recordsRoot $build.Directory.Name
    Assert-Within $destination $projectRoot
    if (Test-Path -LiteralPath $destination) { throw "Records already exist: $destination" }
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $build.Directory.FullName -File) {
        Assert-Within $file.FullName $outputRoot
        if ($file.Extension -eq '.uf2') { Remove-Item -LiteralPath $file.FullName -Force }
        else { Move-Item -LiteralPath $file.FullName -Destination $destination }
    }
    foreach ($directory in Get-ChildItem -LiteralPath $build.Directory.FullName -Force | Where-Object PSIsContainer) {
        Assert-Within $directory.FullName $outputRoot
        Move-Item -LiteralPath $directory.FullName -Destination $destination
    }
    Assert-Within $build.Directory.FullName $outputRoot
    [IO.File]::SetAttributes($build.Directory.FullName, ([IO.File]::GetAttributes($build.Directory.FullName) -band (-bnot [IO.FileAttributes]::ReadOnly)))
    [IO.Directory]::Delete($build.Directory.FullName) # Empty only: never recursively delete a build tree.
}
foreach ($entry in $originalEntries | Where-Object { $_.FullName -notin $builds.Directory.FullName }) {
    Assert-Within $entry.FullName $outputRoot
    $destination = Join-Path $recordsRoot $entry.Name
    Assert-Within $destination $projectRoot
    if (Test-Path -LiteralPath $destination) { throw "Records already exist: $destination" }
    Move-Item -LiteralPath $entry.FullName -Destination $destination
}
Write-Output 'Output organized: one current build, four archives; other files moved to Build Records.'
