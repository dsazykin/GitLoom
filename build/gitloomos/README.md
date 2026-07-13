# GitLoomOS payload build (P2-21)

Reproducible build of `GitLoomOS.tar.gz` — the WSL2 root filesystem GitLoom imports as the
`GitLoomEnv` distro (P2-05). This is the "payload pipeline" half of P2-21.

## Files

| File | Role |
|---|---|
| `Dockerfile` | Rootfs recipe. Base image pinned **by digest**; installs the pinned package set; writes `/etc/wsl.conf` (boot dockerd + default `gitloom` user) and the versioned `/etc/gitloomos-release` stamp. |
| `packages.pinned.txt` | The exact `package=version` set. Bump deliberately per `docs/gitloomos-updates.md`; never float. |
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

Determinism comes from three pins plus a deterministic repack:

1. **Base image pinned by digest** (not a tag) in the `FROM` line.
2. **Packages pinned to exact versions** in `packages.pinned.txt`.
3. **Deterministic tar**: `--sort=name --mtime=@0 --owner=0 --group=0 --numeric-owner --format=gnu`,
   then `gzip -n` (no timestamp/name in the gzip header).

The `build-inputs hash` = `sha256(Dockerfile + packages.pinned.txt)` is stamped into
`/etc/gitloomos-release`, so the payload self-describes exactly what produced it. CI
(`payload-reproducible` job) builds the tarball **twice** and asserts an identical sha256.

> Caveat: byte-for-byte reproducibility also depends on the pinned apt package versions remaining
> fetchable from the archive snapshot; when Ubuntu retires a version, bump the pin in the same commit
> as the base-digest bump. This is the documented CVE cadence, not a floating input.
