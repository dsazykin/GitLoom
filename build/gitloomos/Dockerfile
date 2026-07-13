# GitLoomOS payload rootfs (P2-21 §3.5). Built into a WSL2-importable GitLoomOS.tar.gz.
#
# Reproducibility (invariant 2): the base image is pinned by DIGEST (not a floating tag), apt is
# pinned at a frozen snapshot.debian.org TIMESTAMP (so a package *name* resolves to one deterministic
# version that is always still fetchable), and build.sh repacks the exported rootfs deterministically
# (sorted, fixed mtime, numeric owner, gzip -n). Given the same pinned inputs the output tarball hash
# is stable — CI proves it by double-building and comparing sha256.
#
# Why snapshot instead of exact per-version apt pins: Debian/Ubuntu mirrors drop old exact versions,
# so `apt-get install pkg=exact-version` stops being fetchable over time (non-reproducible by
# construction). snapshot.debian.org is a frozen, timestamped archive — deterministic AND durable.

# Pin by digest. Update deliberately per docs/gitloomos-updates.md; never use a floating tag.
FROM debian:bookworm-slim@sha256:90522eeb7e5923ee2b871c639059537b30521272f10ca86fdbbbb2b75a8c40cd

# The frozen Debian archive instant every package version is resolved against. Bump deliberately
# (docs/gitloomos-updates.md) — a new snapshot is the reproducible way to move the version floor / take
# CVE fixes. Must be a real snapshot.debian.org timestamp where bookworm exists (YYYYMMDDTHHMMSSZ).
ARG DEBIAN_SNAPSHOT=20250601T000000Z

ARG GITLOOMOS_VERSION=0.0.0-dev
ARG BUILD_INPUTS_HASH=unknown

# Deterministic apt: no recommends, no lists left behind, fixed locale/timezone.
ENV DEBIAN_FRONTEND=noninteractive
ENV TZ=Etc/UTC

COPY packages.pinned.txt /tmp/packages.pinned.txt

# Point apt at the frozen snapshot for bookworm main + security, then install package NAMES (the
# snapshot freezes the versions). The snapshot is signed by the normal Debian archive keys already
# present in debian-archive-keyring, so this stays authenticated — no AllowUnauthenticated. Its
# Release files are old, so Check-Valid-Until must be off; snapshot.debian.org is slow/rate-limited,
# hence the generous retries/timeout.
RUN set -eux; \
    rm -f /etc/apt/sources.list /etc/apt/sources.list.d/debian.sources; \
    printf 'deb http://snapshot.debian.org/archive/debian/%s/ bookworm main\ndeb http://snapshot.debian.org/archive/debian-security/%s/ bookworm-security main\n' \
      "${DEBIAN_SNAPSHOT}" "${DEBIAN_SNAPSHOT}" > /etc/apt/sources.list; \
    printf 'Acquire::Check-Valid-Until "false";\nAcquire::Retries "6";\nAcquire::http::Timeout "120";\nAcquire::https::Timeout "120";\n' \
      > /etc/apt/apt.conf.d/99gitloomos-snapshot; \
    apt-get update; \
    # Install package names (comments/blanks stripped); the snapshot resolves each to one version.
    pkgs="$(grep -vE '^\s*#' /tmp/packages.pinned.txt | grep -vE '^\s*$' | tr '\n' ' ')"; \
    apt-get install -y --no-install-recommends $pkgs; \
    # Provide the `fd` name the toolchain expects (Debian ships it as fdfind).
    ln -sf "$(command -v fdfind)" /usr/local/bin/fd; \
    apt-get clean; \
    rm -rf /var/lib/apt/lists/* /tmp/packages.pinned.txt

# The unprivileged VM user the daemon and agents run as (uid pinned for reproducibility).
RUN set -eux; \
    groupadd -g 1000 gitloom; \
    useradd -u 1000 -g 1000 -m -s /bin/bash gitloom; \
    usermod -aG docker gitloom

# /etc/wsl.conf: boot dockerd, default to the gitloom user (P2-05 FirstBootStep expects this).
RUN printf '[boot]\ncommand = service docker start\n\n[user]\ndefault = gitloom\n' > /etc/wsl.conf

# The versioned release stamp (P2-21: /etc/gitloomos-release carries version + build-inputs hash).
RUN printf 'GITLOOMOS_VERSION=%s\nBUILD_INPUTS_HASH=%s\nBASE_IMAGE=debian:bookworm-slim@sha256:90522eeb7e5923ee2b871c639059537b30521272f10ca86fdbbbb2b75a8c40cd\nDEBIAN_SNAPSHOT=%s\n' \
    "${GITLOOMOS_VERSION}" "${BUILD_INPUTS_HASH}" "${DEBIAN_SNAPSHOT}" > /etc/gitloomos-release

CMD ["/bin/bash"]
