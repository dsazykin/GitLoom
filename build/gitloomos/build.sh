#!/usr/bin/env bash
# GitLoomOS payload build (P2-21 §3.5). Produces a WSL2-importable, hash-stable GitLoomOS.tar.gz.
#
# Reproducibility (invariant 2): pinned base digest + pinned packages + a DETERMINISTIC repack of the
# exported rootfs (sorted entries, fixed mtime, numeric 0/0 owner, gzip -n so no timestamp/name lands
# in the gzip header). Given the same pinned inputs the sha256 is identical — CI double-builds and
# diffs. The build-inputs hash (Dockerfile + packages.pinned.txt) is stamped into /etc/gitloomos-release.
#
# usage: build.sh [OUTPUT_DIR]   (default: build/gitloomos/out)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
OUT_DIR="${1:-$HERE/out}"
VERSION="${GITLOOMOS_VERSION:-$(cat "$HERE/VERSION" 2>/dev/null || echo 0.0.0-dev)}"

mkdir -p "$OUT_DIR"

# Build-inputs hash: the pinned inputs that define the payload. Any change → new hash → deliberate bump.
INPUTS_HASH="$(cat "$HERE/Dockerfile" "$HERE/packages.pinned.txt" "$HERE/gitloomd.service" | sha256sum | cut -d' ' -f1)"
echo "GitLoomOS version : $VERSION"
echo "Build-inputs hash : $INPUTS_HASH"

IMAGE_TAG="gitloomos-payload:${VERSION}"

# Publish the GitLoom daemon (gitloomd) into the docker build context BEFORE `docker build`. It is a
# self-contained linux-x64 build (the rootfs has no .NET runtime), published DETERMINISTICALLY so it
# does not undermine invariant 2: Deterministic + ContinuousIntegrationBuild normalize the compiler
# output, no ReadyToRun (its native codegen is non-reproducible), no single-file, no PDBs. Two
# back-to-back publishes are byte-identical, so the daemon layer keeps the whole tarball hash-stable —
# no scope carve-out needed in the payload-reproducible CI job. The apphost is renamed to `gitloomd`
# (it loads GitLoom.Server.dll by its embedded name, so the rename is transparent) so the running
# process comm is exactly `gitloomd` — what P2-05's `pgrep -x gitloomd` matches.
DAEMON_CTX="$HERE/payload/daemon"
echo "==> Publishing gitloomd (GitLoom.Server, linux-x64 self-contained, deterministic)…"
rm -rf "$HERE/payload"
mkdir -p "$DAEMON_CTX"
dotnet publish "$REPO_ROOT/GitLoom.Server/GitLoom.Server.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false \
  -p:DebugType=none -p:DebugSymbols=false \
  -p:Deterministic=true -p:ContinuousIntegrationBuild=true \
  -o "$DAEMON_CTX"
mv "$DAEMON_CTX/GitLoom.Server" "$DAEMON_CTX/gitloomd"
chmod 0755 "$DAEMON_CTX/gitloomd"

echo "==> Building rootfs image (pinned base + pinned packages)…"
DOCKER_BUILDKIT=1 docker build \
  --build-arg "GITLOOMOS_VERSION=${VERSION}" \
  --build-arg "BUILD_INPUTS_HASH=${INPUTS_HASH}" \
  -t "$IMAGE_TAG" \
  "$HERE"

echo "==> Exporting + deterministically repacking rootfs…"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK" "$HERE/payload"' EXIT

CID="$(docker create "$IMAGE_TAG")"
docker export "$CID" -o "$WORK/rootfs.tar"
docker rm "$CID" >/dev/null

mkdir -p "$WORK/rootfs"
tar -xf "$WORK/rootfs.tar" -C "$WORK/rootfs"

# Deterministic tar: sort names, zero mtime, numeric 0/0 owner, GNU format; gzip -n strips header
# timestamp/name so the .gz is byte-stable.
TARBALL="$OUT_DIR/GitLoomOS.tar.gz"
tar --sort=name \
    --mtime='@0' \
    --owner=0 --group=0 --numeric-owner \
    --format=gnu \
    -C "$WORK/rootfs" -cf - . \
  | gzip -n -9 > "$TARBALL"

SHA="$(sha256sum "$TARBALL" | cut -d' ' -f1)"
echo "$SHA  GitLoomOS.tar.gz" > "$OUT_DIR/GitLoomOS.tar.gz.sha256"
printf 'GITLOOMOS_VERSION=%s\nBUILD_INPUTS_HASH=%s\nTARBALL_SHA256=%s\n' \
  "$VERSION" "$INPUTS_HASH" "$SHA" > "$OUT_DIR/gitloomos-release"

echo "==> Done."
echo "    tarball : $TARBALL"
echo "    sha256  : $SHA"
