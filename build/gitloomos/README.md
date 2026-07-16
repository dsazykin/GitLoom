# GitLoomOS payload build (P2-21)

Reproducible build of `GitLoomOS.tar.gz` — the WSL2 root filesystem GitLoom imports as the
`GitLoomEnv` distro (P2-05). This is the "payload pipeline" half of P2-21.

## Files

| File | Role |
|---|---|
| `Dockerfile` | Rootfs recipe (Debian **bookworm**). Base image pinned **by digest**; apt pinned at a frozen **`snapshot.debian.org`** timestamp (`DEBIAN_SNAPSHOT`); installs the package set; `COPY`s the published daemon to `/opt/gitloom/`; enables the `gitloomd` systemd unit; writes `/etc/wsl.conf` (boot systemd + dockerd + default `gitloom` user) and the versioned `/etc/gitloomos-release` stamp. |
| `packages.pinned.txt` | The curated package **name** list (versions come from the snapshot pin, not per-line). Bump the version floor by moving `DEBIAN_SNAPSHOT`; see `docs/gitloomos-updates.md`. Includes `systemd`/`systemd-sysv` so the daemon can be a supervised unit. |
| `gitloomd.service` | The systemd unit that supervises the daemon (`/opt/gitloom/gitloomd`, loopback gRPC, `User=gitloom`, `Restart=on-failure`). Shipped **enabled** (started on first boot). |
| `VERSION` | The GitLoomOS payload version (stamped into the release file). |
| `build.sh` | Publishes the daemon (`dotnet publish GitLoom.Server -r linux-x64`, deterministic) into `payload/daemon/`, builds the image, exports the rootfs, and **deterministically repacks** it into `out/GitLoomOS.tar.gz` (+ `.sha256`, + `gitloomos-release`). |
| `.dockerignore` | Keeps prior `out/` tarballs out of the docker build context (keeps `payload/daemon/`). |

## The GitLoom daemon (`gitloomd`) in the payload

The imported VM must have Docker **and** the GitLoom orchestration daemon running, or the Windows app
connects to nothing and no agent can spawn/verify. So the payload carries the daemon:

- **What.** `gitloomd` is the published `GitLoom.Server` (linux-x64, **self-contained** — the rootfs has
  no .NET runtime). `build.sh` publishes it into the docker context and the Dockerfile `COPY`s it to
  `/opt/gitloom/`. The published apphost is renamed `GitLoom.Server` → `gitloomd` (the apphost loads
  `GitLoom.Server.dll` by its embedded name, so the rename is transparent), so the running process's
  `comm` is exactly `gitloomd` — what P2-05's `pgrep -x gitloomd` / `pgrep -f gitloomd` health checks
  match.
- **How it starts.** `/etc/wsl.conf` sets `[boot] systemd=true`, and `gitloomd.service` is shipped
  **enabled** (its `multi-user.target.wants` symlink is written at build time). So on first boot systemd
  brings the daemon up automatically, alongside dockerd (still started by the proven `[boot] command =
  service docker start`). P2-05 `StartDaemonStep`'s `systemctl start gitloomd` is then only a repair
  path — `pgrep` already matches on a healthy boot.
- **Reachability.** The daemon binds **loopback `127.0.0.1:5250` only** (invariant 2). WSL2
  `localhostForwarding` relays the Windows app's `127.0.0.1:5250` connection into the in-VM listener, so
  no non-loopback bind is ever needed.

## Reproducibility of the daemon layer (interaction with invariant 2)

A freshly-published .NET binary is not byte-identical build-to-build **by default** (fresh MVID,
embedded timestamps). Since the daemon is `COPY`'d into the image, it is part of the exported rootfs and
therefore part of the compared tarball hash — so it must be **deterministic** or it would break the
`payload-reproducible` CI job. `build.sh` publishes it deterministically and this keeps the WHOLE tarball
hash-stable (no scope carve-out in the CI job):

- `-p:Deterministic=true -p:ContinuousIntegrationBuild=true` — normalized MVID + embedded paths, zeroed
  PE timestamps.
- `-p:PublishReadyToRun=false` — R2R native codegen is **not** reproducible.
- `-p:PublishSingleFile=false`, `-p:PublishTrimmed=false`, `-p:DebugType=none -p:DebugSymbols=false` —
  no single-file bundle, no trimming, no PDBs (each a determinism/size variable removed).

Two back-to-back publishes on the same runner are byte-identical (verified: `diff -rq` clean across all
363 published files), so the tarball's sha256 is stable across the CI double-build. The `build-inputs
hash` in `/etc/gitloomos-release` now also covers `gitloomd.service` (a pinned input); the daemon
**binary** is a versioned build artifact tracked by `GITLOOMOS_VERSION` / the app version, not an apt
pin, so it is deliberately not folded into that hash — its reproducibility is guaranteed by the
deterministic publish above, and the tarball sha256 covers it end-to-end.

## Build

```bash
build/gitloomos/build.sh            # → build/gitloomos/out/GitLoomOS.tar.gz
GITLOOMOS_VERSION=0.1.0 build/gitloomos/build.sh
```

Requires Docker **and** the .NET SDK (pinned by `global.json` — `build.sh` publishes the daemon before
the docker build). The App's OOBE (P2-21) and the P2-05 bootstrapper import the resulting tarball via
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
