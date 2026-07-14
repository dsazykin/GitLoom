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

# The GitLoom daemon (gitloomd = the published GitLoom.Server, linux-x64 self-contained). build.sh
# publishes it deterministically into payload/daemon/ (apphost renamed to `gitloomd`) BEFORE the docker
# build; we COPY it to a stable path. The deterministic repack in build.sh forces uid/gid 0 on every
# entry, so /opt/gitloom is root-owned in the tarball — world-readable + the apphost world-executable is
# all the gitloom-user daemon needs (its writable state lives under $HOME). chmod makes the modes
# explicit (hence hash-stable regardless of the build-context filesystem's umask).
COPY payload/daemon/ /opt/gitloom/
RUN set -eux; \
    chmod -R a+rX /opt/gitloom; \
    chmod 0755 /opt/gitloom/gitloomd

# The systemd unit that supervises the daemon, shipped ENABLED so it starts on first boot (P2-05
# StartDaemonStep's `systemctl start gitloomd` act is then only a repair path — pgrep already matches).
# Enable by writing the [Install] symlink directly (deterministic, no systemctl-in-build dependency).
COPY gitloomd.service /etc/systemd/system/gitloomd.service
RUN set -eux; \
    mkdir -p /etc/systemd/system/multi-user.target.wants; \
    ln -sf /etc/systemd/system/gitloomd.service \
      /etc/systemd/system/multi-user.target.wants/gitloomd.service

# /etc/wsl.conf: boot systemd (so the gitloomd unit is supervised + `systemctl start gitloomd` works)
# and default to the gitloom user. NO `[boot] command` for dockerd: the docker.io package enables
# docker.service/docker.socket under systemd, so systemd already starts dockerd on boot — a boot command
# that also ran `service docker start` DOUBLE-started it, leaving a stale /var/run/docker.pid ("pid file
# found") and tripping systemd's "start request repeated too quickly". FirstBootStep also repairs this
# file idempotently on the client side for VMs already imported with the old boot command.
RUN printf '[boot]\nsystemd=true\n\n[user]\ndefault = gitloom\n' > /etc/wsl.conf

# The versioned release stamp (P2-21: /etc/gitloomos-release carries version + build-inputs hash).
RUN printf 'GITLOOMOS_VERSION=%s\nBUILD_INPUTS_HASH=%s\nBASE_IMAGE=debian:bookworm-slim@sha256:90522eeb7e5923ee2b871c639059537b30521272f10ca86fdbbbb2b75a8c40cd\nDEBIAN_SNAPSHOT=%s\n' \
    "${GITLOOMOS_VERSION}" "${BUILD_INPUTS_HASH}" "${DEBIAN_SNAPSHOT}" > /etc/gitloomos-release

CMD ["/bin/bash"]
