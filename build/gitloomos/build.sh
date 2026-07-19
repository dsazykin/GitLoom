#!/usr/bin/env bash
# GitLoomOS payload build (P2-21 §3.5). Produces a WSL2-importable, hash-stable GitLoomOS.tar.gz.
#
# Reproducibility (invariant 2): pinned base digest + pinned packages + a DETERMINISTIC repack of the
# exported rootfs (sorted entries, fixed mtime, numeric owner carried from the image, gzip -n so no
# timestamp/name lands in the gzip header). Given the same pinned inputs the sha256 is identical — CI
# double-builds and diffs. The build-inputs hash (Dockerfile + packages.pinned.txt) is stamped into
# /etc/gitloomos-release.
#
# The extract+repack runs AS ROOT INSIDE the pinned image, and that is load-bearing twice over:
#   1. Ownership. This step used to repack with `--owner=0 --group=0`, flattening EVERY entry to
#      root:root — including /home/gitloom, which the Dockerfile creates as gitloom:gitloom via
#      `useradd -m`. gitloomd runs as uid 1000 with HOME=/home/gitloom, so its very first act
#      (SessionTokenFile.Create → Directory.CreateDirectory("~/.gitloom")) hit EACCES → unhandled
#      exception out of ConfigureServices → systemd restart loop. The image was always correct; the
#      PACKAGING broke it, which is why the daemon smoke (run against the image) never saw it.
#   2. Modes. Extracting as a non-root user makes GNU tar apply the umask and drop setuid/setgid/sticky
#      bits — /tmp shipped 0755 instead of 1777. Root extraction with -p keeps them.
# Determinism is strengthened, not weakened: tar and gzip now come from the pinned image rather than
# from whatever the build host happens to have installed.
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
# output, no ReadyToRun (its native codegen is non-reproducible), no single-file. Deterministic PORTABLE
# PDBs DO ship (DebugType=portable): the daemon logging records ex.StackTrace, and the PDBs turn those
# method-name-only frames into `…SpawnAsync() in AgentSpawnService.cs:line N` file:line diagnostics. They
# stay hash-stable because Deterministic + ContinuousIntegrationBuild normalize the compiler output and
# the PDB GUID + embedded source paths, so two back-to-back publishes are byte-identical (Mainguard.Server.pdb
# included) and the daemon layer keeps the whole tarball hash-stable — no scope carve-out needed in the
# payload-reproducible CI job. The apphost is renamed to `gitloomd`
# (it loads Mainguard.Server.dll by its embedded name, so the rename is transparent) so the running
# process comm is exactly `gitloomd` — what P2-05's `pgrep -x gitloomd` matches.
DAEMON_CTX="$HERE/payload/daemon"
echo "==> Publishing gitloomd (Mainguard.Server, linux-x64 self-contained, deterministic)…"
rm -rf "$HERE/payload"
mkdir -p "$DAEMON_CTX"
dotnet publish "$REPO_ROOT/Mainguard.Server/Mainguard.Server.csproj" \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:PublishTrimmed=false \
  -p:DebugType=portable -p:DebugSymbols=true \
  -p:Deterministic=true -p:ContinuousIntegrationBuild=true \
  -o "$DAEMON_CTX"
mv "$DAEMON_CTX/Mainguard.Server" "$DAEMON_CTX/gitloomd"
chmod 0755 "$DAEMON_CTX/gitloomd"

echo "==> Building rootfs image (pinned base + pinned packages)…"
DOCKER_BUILDKIT=1 docker build \
  --build-arg "GITLOOMOS_VERSION=${VERSION}" \
  --build-arg "BUILD_INPUTS_HASH=${INPUTS_HASH}" \
  -t "$IMAGE_TAG" \
  "$HERE"

echo "==> Exporting + deterministically repacking rootfs…"
WORK="$(mktemp -d)"
# Tolerant cleanup: the extracted rootfs is root-owned, and on a failure path the in-container chown
# below never runs, so a plain `rm -rf` would fail and mask the real error.
trap 'rm -rf "$WORK" "$HERE/payload" 2>/dev/null || true' EXIT

CID="$(docker create "$IMAGE_TAG")"
docker export "$CID" -o "$WORK/rootfs.tar"
docker rm "$CID" >/dev/null

# Extract + repack as root inside the pinned image (see the header): -p and --numeric-owner on the way
# in preserve the image's real uid/gid and high mode bits; sorted names + zero mtime + numeric owner +
# gzip -n on the way out keep the tarball byte-stable. The final chown hands the artifact back to the
# invoking user so the host-side mv and the cleanup trap work unprivileged.
TARBALL="$OUT_DIR/GitLoomOS.tar.gz"
docker run --rm \
  -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
  -v "$WORK:/work" \
  "$IMAGE_TAG" bash -c '
    set -euo pipefail
    mkdir -p /work/rootfs
    tar -p --numeric-owner -xf /work/rootfs.tar -C /work/rootfs
    tar --sort=name \
        --mtime="@0" \
        --numeric-owner \
        --format=gnu \
        -C /work/rootfs -cf - . \
      | gzip -n -9 > /work/GitLoomOS.tar.gz
    rm -rf /work/rootfs /work/rootfs.tar
    chown "$HOST_UID:$HOST_GID" /work/GitLoomOS.tar.gz
  '
mv "$WORK/GitLoomOS.tar.gz" "$TARBALL"

SHA="$(sha256sum "$TARBALL" | cut -d' ' -f1)"
echo "$SHA  GitLoomOS.tar.gz" > "$OUT_DIR/GitLoomOS.tar.gz.sha256"
printf 'GITLOOMOS_VERSION=%s\nBUILD_INPUTS_HASH=%s\nTARBALL_SHA256=%s\n' \
  "$VERSION" "$INPUTS_HASH" "$SHA" > "$OUT_DIR/gitloomos-release"

echo "==> Done."
echo "    tarball : $TARBALL"
echo "    sha256  : $SHA"
