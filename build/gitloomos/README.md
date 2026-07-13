# GitLoomOS payload build (P2-21)

Reproducible build of `GitLoomOS.tar.gz` — the WSL2 root filesystem GitLoom imports as the
`GitLoomEnv` distro (P2-05). This is the "payload pipeline" half of P2-21.

## Files

| File | Role |
|---|---|
| `Dockerfile` | Rootfs recipe (Debian **bookworm**). Base image pinned **by digest**; apt pinned at a frozen **`snapshot.debian.org`** timestamp (`DEBIAN_SNAPSHOT`); installs the package set; writes `/etc/wsl.conf` (boot dockerd + default `gitloom` user) and the versioned `/etc/gitloomos-release` stamp. |
| `packages.pinned.txt` | The curated package **name** list (versions come from the snapshot pin, not per-line). Bump the version floor by moving `DEBIAN_SNAPSHOT`; see `docs/gitloomos-updates.md`. |
| `VERSION` | The GitLoomOS payload version (stamped into the release file). |
| `build.sh` | Builds the image, exports the rootfs, and **deterministically repacks** it into `out/GitLoomOS.tar.gz` (+ `.sha256`, + `gitloomos-release`). |

## Build

```bash
build/gitloomos/build.sh            # → build/gitloomos/out/GitLoomOS.tar.gz
GITLOOMOS_VERSION=0.1.0 build/gitloomos/build.sh
```

Requires Docker. The App's OOBE (P2-21) and the P2-05 bootstrapper import the resulting tarball via
`wsl --import GitLoomEnv <installDir> GitLoomOS.tar.gz --version 2`.

## Reproducibility (invariant 2 — hash-stable given pinned inputs)

Determinism comes from two pins plus a deterministic repack:

1. **Base image pinned by digest** (not a tag) in the `FROM` line — a *dated* `debian:bookworm-…-slim`
   image that predates the snapshot below (so installs only ever upgrade, never hit an impossible
   downgrade).
2. **Apt pinned at a frozen `snapshot.debian.org` timestamp** (`DEBIAN_SNAPSHOT`, e.g.
   `20250601T000000Z`) for `bookworm main` + `bookworm-security`. The snapshot freezes the whole
   archive at one instant, so installing a package **name** resolves to one deterministic version —
   and, unlike an exact per-version pin, that version stays fetchable forever (mirrors never retire a
   snapshot). The snapshot is signed by the normal Debian archive keys already in
   `debian-archive-keyring`, so the fetch stays authenticated (no `AllowUnauthenticated`); its Release
   files are old, so `Acquire::Check-Valid-Until` is turned off.
3. **Deterministic tar**: `--sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner --format=gnu`,
   then `gzip -n` (no timestamp/name in the gzip header).

The `build-inputs hash` = `sha256(Dockerfile + packages.pinned.txt)` is stamped into
`/etc/gitloomos-release` (alongside `DEBIAN_SNAPSHOT` and the base digest), so the payload
self-describes exactly what produced it. CI (`payload-reproducible` job) builds the tarball **twice**
and asserts an identical sha256.

### Bumping the version floor (CVE cadence)

To take newer packages (security fixes), move `DEBIAN_SNAPSHOT` to a **later** real snapshot timestamp
(`YYYYMMDDTHHMMSSZ` — any instant where bookworm exists; snapshot.debian.org redirects to the nearest
snapshot at or before it, deterministically). If the new snapshot predates the pinned base image you
will hit downgrade conflicts, so bump the base `FROM` digest to a dated `debian:bookworm-…-slim` at or
before the snapshot in the **same commit**. Both are deliberate, digest/timestamp-pinned inputs — never
floating — so the reproducibility invariant (pinned in → stable out) still holds.

> Note: snapshot.debian.org is rate-limited and slow (the package-list fetch can take a couple of
> minutes); the Dockerfile sets generous `Acquire::Retries`/`Timeout` to absorb that.
