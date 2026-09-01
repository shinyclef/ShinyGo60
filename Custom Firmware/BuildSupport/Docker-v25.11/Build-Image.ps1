[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$builderName = 'shinygo60-v25-11'
$imageName = 'shinygo60-builder:v25.11'
$scriptDirectory = $PSScriptRoot

docker info --format '{{.ServerVersion}}' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Desktop is not running.'
}

docker buildx inspect $builderName *> $null
if ($LASTEXITCODE -ne 0) {
    docker buildx create --name $builderName --driver docker-container | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not create the isolated Docker builder '$builderName'."
    }
}

docker buildx inspect $builderName --bootstrap | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not start the isolated Docker builder '$builderName'."
}

docker buildx build --builder $builderName --load --progress plain --tag $imageName $scriptDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'The ShinyGo60 Docker image build failed.'
}
