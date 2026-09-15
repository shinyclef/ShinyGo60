[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ManifestPath,
    [string] $ConfigurationPath,
    [ValidateSet('win-x64', 'win-arm64')] [string] $RuntimeIdentifier = 'win-x64',
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifactParent = Join-Path $repositoryRoot 'artifacts'
$artifactRoot = Join-Path $artifactParent 'ShinyGo60 Companion'
$stageRoot = Join-Path $artifactParent ('.ShinyGo60-Companion-stage-' + [Guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $artifactParent ('.ShinyGo60-Companion-previous-' + [Guid]::NewGuid().ToString('N'))
$manifestSource = (Resolve-Path -LiteralPath $ManifestPath).Path
$configSource = if ($ConfigurationPath) { (Resolve-Path -LiteralPath $ConfigurationPath).Path }
    elseif (Test-Path -LiteralPath (Join-Path $artifactRoot 'companion-settings.json')) { Join-Path $artifactRoot 'companion-settings.json' }
    else { Join-Path $PSScriptRoot 'companion-settings.example.json' }

function Assert-ArtifactPath([string] $Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($absolute) -ne [IO.Path]::GetFullPath($artifactParent)) {
        throw "Unexpected artifact path: $absolute"
    }
    if ((Test-Path -LiteralPath $absolute) -and (Get-Item -LiteralPath $absolute).LinkType) {
        throw "Refusing to replace a linked artifact directory: $absolute"
    }
}

try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    & dotnet publish (Join-Path $PSScriptRoot 'ShinyGo60.Companion/ShinyGo60.Companion.csproj') `
        --configuration Release --runtime $RuntimeIdentifier --self-contained (-not $FrameworkDependent).ToString().ToLowerInvariant() `
        --maxcpucount:1 --output $stageRoot
    if ($LASTEXITCODE -ne 0) { throw 'Companion publish failed.' }
    Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $stageRoot 'layout-manifest.json')
    Copy-Item -LiteralPath $configSource -Destination (Join-Path $stageRoot 'companion-settings.json')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Custom Firmware/BuildSupport/PRODUCTION_DIAGNOSTICS.md') `
        -Destination (Join-Path $stageRoot 'Diagnostics Guide.md')

    # Validate the exact packaged configuration before touching an existing installation.
    $verification = Start-Process -FilePath (Join-Path $stageRoot 'ShinyGo60.Companion.exe') `
        -ArgumentList '--verify-package' -WindowStyle Hidden -Wait -PassThru
    if ($verification.ExitCode -ne 0) { throw "Packaged configuration validation failed (exit $($verification.ExitCode))." }

    Assert-ArtifactPath $artifactRoot
    Assert-ArtifactPath $stageRoot
    Assert-ArtifactPath $backupRoot
    $installedExecutable = Join-Path $artifactRoot 'ShinyGo60.Companion.exe'
    $running = Get-Process -Name 'ShinyGo60.Companion' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installedExecutable }
    if ($running) { throw 'Exit the packaged companion before replacing its files, then run publish again.' }

    if (Test-Path -LiteralPath $artifactRoot) { Move-Item -LiteralPath $artifactRoot -Destination $backupRoot }
    try { Move-Item -LiteralPath $stageRoot -Destination $artifactRoot }
    catch {
        if (Test-Path -LiteralPath $backupRoot) { Move-Item -LiteralPath $backupRoot -Destination $artifactRoot }
        throw
    }
    if (Test-Path -LiteralPath $backupRoot) {
        Assert-ArtifactPath $backupRoot
        Remove-Item -LiteralPath $backupRoot -Recurse -Force
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $repositoryRoot 'ShinyGo60 Companion.lnk'))
    $shortcut.TargetPath = $installedExecutable
    $shortcut.WorkingDirectory = $artifactRoot
    $shortcut.Arguments = ''
    $shortcut.IconLocation = "$installedExecutable,0"
    $shortcut.Save()

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $startup = Get-ItemProperty -LiteralPath $runKey -Name 'ShinyGo60 Companion' -ErrorAction SilentlyContinue
    if ($startup.'ShinyGo60 Companion') {
        Set-ItemProperty -LiteralPath $runKey -Name 'ShinyGo60 Companion' -Value ('"' + $installedExecutable + '" --background')
    }
    Write-Output "Published: $artifactRoot"
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Assert-ArtifactPath $stageRoot
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
