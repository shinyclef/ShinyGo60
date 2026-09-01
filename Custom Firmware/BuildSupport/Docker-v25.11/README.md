# ShinyGo60 v25.11 firmware builder

This directory contains the pinned, Go60-only Docker backend selected in Step 3. It builds the MoErgo ZMK `v25.11` source at commit
`11454d23596afbdb06380a1125371b19ab65675c` and defaults to running with networking disabled.

The eventual C# builder will pull a published copy of this image and hide these commands. Building the image locally is a maintainer and recovery workflow, not the
planned first-run experience for ordinary users.

## Build firmware with the installed image

From the repository root:

```powershell
& '.\Custom Firmware\BuildSupport\Docker-v25.11\Build-Firmware.ps1' `
    -Workspace '.\Custom Firmware\Generated\Optimized-v25.11'
```

The workspace must contain the Go60 configuration files in its `config` directory. Use the tracked template under `BuildSupport/Templates/v25.11` when creating a
workspace. The script also mounts the maintained `Custom Firmware/Module` directory read-only, and the template passes it through MoErgo's supported
`extraModules` hook. A successful run writes `go60.uf2` into the workspace root. The container has no network access unless `-AllowNetwork` is supplied explicitly.

## Construct the image locally

Docker Desktop must be running. From the repository root:

```powershell
& '.\Custom Firmware\BuildSupport\Docker-v25.11\Build-Image.ps1'
```

This creates the isolated Buildx builder `shinygo60-v25-11` and loads `shinygo60-builder:v25.11`. Local construction temporarily needs about 10 GB free even
though the retained image is 4.46 GB, because BuildKit holds both its construction cache and the completed image until cleanup.

The image is pinned to:

- base image `nixpkgs/nix:nixos-23.11@sha256:11c1c37da85b27f1b47a7c0fdff8e3cf970cafaac623312dbcf243c84b8756dd`;
- MoErgo ZMK tag `v25.11` and commit `11454d23596afbdb06380a1125371b19ab65675c`;
- final locally validated image ID `sha256:8c05b8af27498f7f42391fa408dfd841fbebfdc70f0d7766a280edd03db98720`.

The image ID is evidence for this exact local construction. A registry digest must be recorded separately when the prebuilt release image is published.

## Scoped cleanup

Remove only the named Buildx builder and its dedicated cache while retaining the firmware image:

```powershell
& '.\Custom Firmware\BuildSupport\Docker-v25.11\Cleanup.ps1'
```

Also remove the managed firmware image:

```powershell
& '.\Custom Firmware\BuildSupport\Docker-v25.11\Cleanup.ps1' -IncludeImage
```

Preview either operation with `-WhatIf`. The script never calls `docker system prune`, `docker builder prune`, or another global cleanup command. Image removal is
refused unless the exact image name has the `io.shinygo60.managed=true` label. Buildx does not provide labels for its helper container and volume, so those resources
are isolated by the exact project-specific builder name instead.

Full measurements and the backend decision are recorded in
[STEP3_BUILD_ENVIRONMENT.md](../STEP3_BUILD_ENVIRONMENT.md).
