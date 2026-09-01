[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $IncludeImage
)

$ErrorActionPreference = 'Stop'

$builderName = 'shinygo60-v25-11'
$imageName = 'shinygo60-builder:v25.11'

if ($PSCmdlet.ShouldProcess("isolated Docker builder '$builderName'", 'Remove its dedicated build cache and builder container')) {
    docker buildx inspect $builderName *> $null
    if ($LASTEXITCODE -eq 0) {
        docker buildx rm $builderName
        if ($LASTEXITCODE -ne 0) {
            throw "Could not remove the isolated Docker builder '$builderName'."
        }
    }
}

if ($IncludeImage -and $PSCmdlet.ShouldProcess("managed Docker image '$imageName'", 'Remove')) {
    $managedLabel = docker image inspect $imageName --format '{{index .Config.Labels "io.shinygo60.managed"}}' 2>$null
    if ($LASTEXITCODE -eq 0) {
        if ($managedLabel -ne 'true') {
            throw "Refusing to remove '$imageName' because it is not labeled as ShinyGo60-managed."
        }

        docker image rm $imageName
        if ($LASTEXITCODE -ne 0) {
            throw "Could not remove the managed Docker image '$imageName'."
        }
    }
}
