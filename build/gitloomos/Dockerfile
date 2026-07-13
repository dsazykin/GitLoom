# GitLoomOS payload rootfs (P2-21 §3.5). Built into a WSL2-importable GitLoomOS.tar.gz.
#
# Reproducibility (invariant 2): the base image is pinned by DIGEST (not a floating tag), packages are
# pinned to exact versions (packages.pinned.txt), and build.sh repacks the exported rootfs
# deterministically (sorted, fixed mtime, numeric owner, gzip -n). Given the same pinned inputs the
# output tarball hash is stable — CI proves it by double-building and comparing sha256.

# Pin by digest. Update deliberately per docs/gitloomos-updates.md; never use a floating tag.
FROM ubuntu:22.04@sha256:0e5e4a57c2499249aafc3b40fcd541e9a456aab7296681a3994d631587203f97

ARG GITLOOMOS_VERSION=0.0.0-dev
ARG BUILD_INPUTS_HASH=unknown

# Deterministic apt: no recommends, no lists left behind, fixed locale/timezone.
ENV DEBIAN_FRONTEND=noninteractive
ENV TZ=Etc/UTC

COPY packages.pinned.txt /tmp/packages.pinned.txt

RUN set -eux; \
    apt-get update; \
    # Install the exact pinned versions (package=version lines, comments/blanks stripped).
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
RUN printf 'GITLOOMOS_VERSION=%s\nBUILD_INPUTS_HASH=%s\nBASE_IMAGE=ubuntu:22.04@sha256:0e5e4a57c2499249aafc3b40fcd541e9a456aab7296681a3994d631587203f97\n' \
    "${GITLOOMOS_VERSION}" "${BUILD_INPUTS_HASH}" > /etc/gitloomos-release

CMD ["/bin/bash"]
