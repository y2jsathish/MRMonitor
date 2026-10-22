#!/usr/bin/env bash
# Usage: deployment/docker/build-image.sh Device 1.4.2 [registry]
set -euo pipefail
svc="$1"; tag="$2"; registry="${3:-atmnexus.azurecr.io}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
proj="src/Services/AtmNexus.Services.${svc}/AtmNexus.Services.${svc}.csproj"
dll="AtmNexus.Services.${svc}.dll"
[ -f "$root/$proj" ] || { echo "no such service project: $proj" >&2; exit 1; }
tmp="$(mktemp)"
sed -e "s#__PROJECT__#${proj}#g" -e "s#__DLL__#${dll}#g" "$root/deployment/docker/Dockerfile.service.template" > "$tmp"
docker build -f "$tmp" --build-arg VERSION="$tag" -t "${registry}/atmnexus/$(echo "$svc" | tr 'A-Z' 'a-z')-service:${tag}" "$root"
rm -f "$tmp"
