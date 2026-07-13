# GitLoom hardened agent base image (P2-07).
#
# Built in CI / the release pipeline ONLY — NEVER at runtime (G-16: a runtime `docker build`
# severs the agent PTY). The toolchain-sideload mechanism (`devbox add <tool>`) needs Nix, so Nix +
# the real Devbox binary + a PINNED nixpkgs are baked at BUILD time (root, with network) so nothing
# installs at runtime — a runtime Nix install needs root+sudo, which the hardened, non-root,
# no-new-privileges jail forbids by design.
#
# The container is spawned with the hardened spec from ContainerSpecBuilder:
#   - no-new-privileges, seccomp denylist (process_vm_readv/process_vm_writev/ptrace), CapDrop ALL
#   - userns remap, memory/pids limits, read-only rootfs (writable surfaces are tmpfs)
#   - the ONLY host bind mount is the ext4 agent worktree at /workspace (G-11); /nix is an EPHEMERAL
#     per-container writable volume pre-populated from this image's baked store (a tmpfs would hide it)
# and reaches the network only through the gitloom-egress-proxy (default-deny: model APIs + package
# registries incl. cache.nixos.org; A6 keeps the git host off the allowlist).

FROM debian:bookworm-slim

# Base tooling only. git + curl for package/dependency fetches through the proxy; xz for nix.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl git xz-utils \
    && rm -rf /var/lib/apt/lists/*

# Two DISTINCT non-root users (G2 control 1): the agent CLI runs as `agent` (uid 1000); the OOB
# supervisor runs as `supervisor` (uid 1001) and solely owns /run/secrets/oob.key (mode 0400) so the
# agent uid can never read the OOB session key K.
RUN groupadd -g 1000 agent      && useradd -u 1000 -g 1000 -m -s /bin/bash agent \
    && groupadd -g 1001 supervisor && useradd -u 1001 -g 1001 -m -s /bin/bash supervisor

# --- Nix (build-time, single-user, no daemon) ---------------------------------------------------
# The Determinate installer with `--init none` installs Nix WITHOUT systemd (correct for a container)
# into /nix, owned root. Flakes on. At runtime /nix is remounted as a writable volume seeded from here.
RUN curl --proto '=https' --tlsv1.2 -sSf -L https://install.determinate.systems/nix \
      | sh -s -- install linux --no-confirm --init none \
        --extra-conf "experimental-features = nix-command flakes" \
        --extra-conf "build-users-group ="
ENV PATH=/nix/var/nix/profiles/default/bin:/usr/local/bin:/usr/bin:/bin

# --- Devbox: the REAL binary (not the self-downloading launcher) ---------------------------------
# get.jetify.com installs a launcher that self-downloads the real binary on first run (blocked in the
# default-deny jail). Run it once here as `agent` so the real binary lands in the cache, then promote it
# to /usr/local/bin so the runtime `devbox` is the real, self-contained binary — no runtime download.
RUN curl -fsSL https://get.jetify.com/devbox | bash -s -- -f \
    && chmod 0755 /usr/local/bin/devbox \
    && HOME=/root /usr/local/bin/devbox version \
    && real="$(find /root/.cache/devbox -type f -name devbox -size +1M | head -1)" \
    && cp "$real" /usr/local/bin/devbox \
    && chmod 0755 /usr/local/bin/devbox \
    && rm -rf /root/.cache/devbox

# --- Pre-baked A6-clean toolchain ---------------------------------------------------------------
# devbox's `add` resolves packages via nixhub → fetches nixpkgs from GITHUB at runtime, which the
# default-deny jail (A6) forbids — and every strict-A6 workaround fights devbox's design. So the
# curated toolchain is baked HERE, at build time (github reachable), into a PERSISTENT nix profile at
# /opt/toolchain (never the agent's tmpfs $HOME, which is wiped at runtime). At runtime the tools are
# already on PATH from the read-only image; nothing resolves or fetches from a git host (A6 intact),
# and a fixed/audited toolchain also shrinks the supply-chain surface the F5 caveat warns about.
# Adding an ARBITRARY new tool at runtime is a documented v1.x item (a daemon-mediated/self-hosted nix
# resolver that keeps the git host off the allowlist). NIXPKGS_REV pins the toolchain for reproducibility.
ARG NIXPKGS_REV=50ab793786d9de88ee30ec4e4c24fb4236fc2674
RUN nix profile install --profile /opt/toolchain \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#jq" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#ripgrep" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#fd" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#tree" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#gnumake" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#nodejs_20" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#python3" \
      "github:NixOS/nixpkgs/${NIXPKGS_REV}#go" \
    && chown -R agent:agent /nix /opt/toolchain
ENV PATH=/opt/toolchain/bin:/nix/var/nix/profiles/default/bin:/usr/local/bin:/usr/bin:/bin

# /workspace is the ext4 worktree bind target; /run/secrets is a tmpfs the daemon populates post-start.
WORKDIR /workspace
USER agent

# The seccomp profile applied to this container is passed by the daemon at create time
# (HostConfig.SecurityOpt: seccomp=<json>). ./seccomp.json IS that profile and the single source of
# truth: it is embedded into GitLoom.Core and returned verbatim by SeccompProfile.Json. It is the
# canonical moby default-deny profile (defaultAction SCMP_ACT_ERRNO) with ptrace/process_vm_readv/
# process_vm_writev removed from every allow rule and explicitly denied.
CMD ["sleep", "infinity"]
