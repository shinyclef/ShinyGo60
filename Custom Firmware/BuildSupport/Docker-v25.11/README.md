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
- initial Step 3 local image ID `sha256:8c05b8af27498f7f42391fa408dfd841fbebfdc70f0d7766a280edd03db98720`;
- Step 8 reconstruction image ID `sha256:efc1cd8775ef49246130c2841cc06638f3e243caffd29c3d587292423706bb41`.
- Step 9 reconstruction image ID `sha256:71f0923c8cbc49c18bcf5f8d168e24f9e0cc0cee087d61e8213242a4ec6d09b6`.
- Step 10 reconstruction image ID `sha256:dc4e878897c99fd48172dbb5ff32ee85f38649214d92e27a328aae7b8cbeac9a`.
- Step 11 reconstruction image ID `sha256:672e5a87f2eebbd85bcb4cd5c1f16d1b00badbe56bfbb34a4b76f2ba1618724b`.

Local image IDs are evidence for their exact constructions. They can vary with BuildKit export metadata even when the pinned base, Dockerfile, build context, and
ZMK source are unchanged. The C# pipeline therefore validates and logs the managed role, ZMK tag, exact source commit, and current local ID. A registry digest must
be recorded separately when the prebuilt release image is published.

## Persistence across Windows restarts

A normal Docker Desktop or Windows restart does not remove the tagged image. Keep Docker Desktop's containerd image-store setting unchanged: switching image
stores can make the other store's images invisible. Avoid Docker Desktop's Clean/Purge data action and global `docker system prune -a`, both of which can remove
the retained image. The project's scoped cleanup preserves it unless `-IncludeImage` is explicitly supplied.

Check the retained image with `docker image inspect shinygo60-builder:v25.11`. If it is genuinely absent, rerun `Build-Image.ps1`; Docker does not keep a reliable
audit identifying which earlier cleanup action removed an image.

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
