[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$artifactParent = Join-Path $repositoryRoot 'artifacts'
$artifactRoot = Join-Path $artifactParent 'ShinyGo60 Builder'
$stageRoot = Join-Path $artifactParent ".ShinyGo60-Builder-stage-$([Guid]::NewGuid().ToString('N'))"
$projectPath = Join-Path $PSScriptRoot 'ShinyGo60.Builder\ShinyGo60.Builder.csproj'

function Copy-Directory {
    param(
        [Parameter(Mandatory)]
        [string] $Source,

        [Parameter(Mandatory)]
        [string] $Destination
    )

    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse
}

try {
    New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

    & dotnet publish $projectPath `
        --configuration Release `
        --maxcpucount:1 `
        --runtime $RuntimeIdentifier `
        --self-contained true `
        --output $stageRoot `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw 'The self-contained builder publish failed.'
    }

    $firmwareSupport = Join-Path $stageRoot 'Custom Firmware\BuildSupport'
    Copy-Directory `
        (Join-Path $repositoryRoot 'Custom Firmware\BuildSupport\Templates') `
        (Join-Path $firmwareSupport 'Templates')
    Copy-Directory `
        (Join-Path $repositoryRoot 'Custom Firmware\BuildSupport\Docker-v25.11') `
        (Join-Path $firmwareSupport 'Docker-v25.11')
    Copy-Directory `
        (Join-Path $repositoryRoot 'Custom Firmware\Module') `
        (Join-Path $stageRoot 'Custom Firmware\Module')

    New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot 'Input') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot 'Output') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Input\README.md') -Destination (Join-Path $stageRoot 'Input\README.md')
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot 'Custom Firmware\BuildSupport\STEP15_ONE_CLICK_BUILDER.md') `
        -Destination (Join-Path $stageRoot 'Builder Guide.md')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $stageRoot 'LICENSE.txt')
    # Read the maintained workspace on every build instead of freezing firmware at publish time.
    Set-Content -LiteralPath (Join-Path $stageRoot 'firmware-source.txt') -Value $repositoryRoot -Encoding utf8

    $publishedExecutable = Join-Path $stageRoot 'ShinyGo60.Builder.exe'
    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        throw 'The publish completed without creating ShinyGo60.Builder.exe.'
    }

    $unexpectedRuntimeFiles = Get-ChildItem -LiteralPath $stageRoot -File |
        Where-Object { $_.Extension -in '.dll', '.deps.json', '.runtimeconfig.json' }
    if ($unexpectedRuntimeFiles) {
        throw 'The builder was not packaged as one self-contained application file.'
    }

    if (Test-Path -LiteralPath $artifactRoot) {
        $resolvedArtifactParent = (Resolve-Path -LiteralPath $artifactParent).Path
        $resolvedArtifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
        if ((Split-Path -Parent $resolvedArtifactRoot) -ne $resolvedArtifactParent -or
            (Split-Path -Leaf $resolvedArtifactRoot) -ne 'ShinyGo60 Builder') {
            throw "Refusing to replace unexpected artifact path '$resolvedArtifactRoot'."
        }

        $installedExecutable = Join-Path $resolvedArtifactRoot 'ShinyGo60.Builder.exe'
        $runningBuilder = Get-Process -Name 'ShinyGo60.Builder' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $installedExecutable }
        if ($runningBuilder) {
            throw 'Close the packaged ShinyGo60 Builder before publishing an updated copy.'
        }

        foreach ($managedName in @('Custom Firmware', 'Builder Guide.md', 'LICENSE.txt')) {
            $managedPath = Join-Path $resolvedArtifactRoot $managedName
            if (Test-Path -LiteralPath $managedPath) {
                $resolvedManagedPath = (Resolve-Path -LiteralPath $managedPath).Path
                if ((Split-Path -Parent $resolvedManagedPath) -ne $resolvedArtifactRoot -or
                    (Get-Item -LiteralPath $resolvedManagedPath).LinkType) {
                    throw "Refusing to replace unexpected package path '$resolvedManagedPath'."
                }
                Remove-Item -LiteralPath $managedPath -Recurse -Force
            }
        }
    }

    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $artifactRoot 'Input') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $artifactRoot 'Output') | Out-Null
    Copy-Item -LiteralPath (Join-Path $stageRoot 'Custom Firmware') -Destination $artifactRoot -Recurse
    Copy-Item -LiteralPath (Join-Path $stageRoot 'Builder Guide.md') -Destination $artifactRoot
    Copy-Item -LiteralPath (Join-Path $stageRoot 'LICENSE.txt') -Destination $artifactRoot
    Copy-Item -LiteralPath (Join-Path $stageRoot 'firmware-source.txt') -Destination $artifactRoot -Force
    Copy-Item -LiteralPath (Join-Path $stageRoot 'Input\README.md') -Destination (Join-Path $artifactRoot 'Input') -Force
    Copy-Item -LiteralPath $publishedExecutable -Destination $artifactRoot -Force

    if (-not (Test-Path -LiteralPath (Join-Path $artifactRoot 'ShinyGo60.Builder.exe') -PathType Leaf)) {
        throw 'The staged builder could not be copied into the release folder.'
    }

    $shortcutShell = New-Object -ComObject WScript.Shell
    $shortcut = $shortcutShell.CreateShortcut((Join-Path $repositoryRoot 'ShinyGo60 Builder.lnk'))
    $shortcut.TargetPath = Join-Path $artifactRoot 'ShinyGo60.Builder.exe'
    $shortcut.WorkingDirectory = $repositoryRoot
    $shortcut.Arguments = ''
    $shortcut.Save()

    Write-Output $artifactRoot
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        $resolvedStage = (Resolve-Path -LiteralPath $stageRoot).Path
        if ((Split-Path -Parent $resolvedStage) -ne [IO.Path]::GetFullPath($artifactParent) -or
            (Get-Item -LiteralPath $resolvedStage).LinkType) {
            throw "Refusing to remove unexpected staging path '$resolvedStage'."
        }
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
