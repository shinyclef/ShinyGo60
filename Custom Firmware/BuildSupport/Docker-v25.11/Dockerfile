# syntax=docker/dockerfile:1.7

FROM nixpkgs/nix:nixos-23.11@sha256:11c1c37da85b27f1b47a7c0fdff8e3cf970cafaac623312dbcf243c84b8756dd

ARG ZMK_TAG=v25.11
ARG ZMK_COMMIT=11454d23596afbdb06380a1125371b19ab65675c

LABEL org.opencontainers.image.title="ShinyGo60 firmware builder" \
      org.opencontainers.image.description="Pinned MoErgo Go60 v25.11 firmware build environment" \
      org.opencontainers.image.source="https://github.com/moergo-sc/zmk" \
      org.opencontainers.image.version="v25.11" \
      org.opencontainers.image.revision="${ZMK_COMMIT}" \
      io.shinygo60.managed="true" \
      io.shinygo60.role="firmware-builder" \
      io.shinygo60.zmk.tag="${ZMK_TAG}" \
      io.shinygo60.zmk.commit="${ZMK_COMMIT}"

ENV PATH=/root/.nix-profile/bin:/usr/bin:/bin

SHELL ["/bin/bash", "-c"]

RUN <<EOF
    set -euo pipefail

    mkdir -p /etc/nix /config
    cat > /etc/nix/nix.conf <<'NIX_CONFIG'
substituters = https://cache.nixos.org https://moergo-glove80-zmk-dev.cachix.org
trusted-public-keys = cache.nixos.org-1:6NCHdD59X431o0gWypbMrAURkbJ16ZPMQFGspcDShjY= moergo-glove80-zmk-dev.cachix.org-1:D8fhPJ1c3gkuckuyBLRtGYarCVptIDn4g5k8u91NbyM=
NIX_CONFIG

    git clone --depth 1 --branch "${ZMK_TAG}" https://github.com/moergo-sc/zmk /src
    test "$(git -C /src rev-parse HEAD)" = "${ZMK_COMMIT}"
    printf '%s\n' "${ZMK_COMMIT}" > /src/.shinygo60-zmk-commit

    cd /src
    nix-shell --run true -A zmk ./default.nix

    rm -rf /root/.cache /tmp/*
EOF

COPY --chmod=755 entrypoint.sh /usr/local/bin/shinygo60-build

ENTRYPOINT ["/usr/local/bin/shinygo60-build"]
