[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Workspace,

    [switch] $AllowNetwork,

    [switch] $ConnectionDiagnostics
)

$ErrorActionPreference = 'Stop'

$imageName = 'shinygo60-builder:v25.11'
$resolvedWorkspace = (Resolve-Path -LiteralPath $Workspace).Path
$modulePath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\Module')).Path
$networkMode = if ($AllowNetwork) { 'default' } else { 'none' }
$diagnosticsMode = if ($ConnectionDiagnostics) { '1' } else { '0' }

$managedLabel = docker image inspect $imageName --format '{{index .Config.Labels "io.shinygo60.managed"}}'
if ($LASTEXITCODE -ne 0 -or $managedLabel -ne 'true') {
    throw "The expected managed image '$imageName' is unavailable. Run Build-Image.ps1 first."
}

docker run --rm `
    --network $networkMode `
    --label io.shinygo60.managed=true `
    --label io.shinygo60.role=firmware-build-job `
    --mount "type=bind,source=$resolvedWorkspace,target=/config" `
    --mount "type=bind,source=$modulePath,target=/shinygo60-module,readonly" `
    -e UID=0 `
    -e GID=0 `
    -e "SHINYGO60_CONNECTION_DIAGNOSTICS=$diagnosticsMode" `
    --mount "type=bind,source=$PSScriptRoot\entrypoint.sh,target=/usr/local/bin/shinygo60-build,readonly" `
    $imageName

if ($LASTEXITCODE -ne 0) {
    throw 'The Go60 firmware build failed.'
}
