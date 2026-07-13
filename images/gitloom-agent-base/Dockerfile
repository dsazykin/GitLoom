# GitLoom hardened agent base image (P2-07).
#
# Built in CI / the release pipeline ONLY — NEVER at runtime (G-16: a runtime `docker build`
# severs the agent PTY). Toolchains are NOT baked in; they sideload at runtime via `devbox add`
# into the running container, so this image stays static.
#
# The container is spawned with the hardened spec from ContainerSpecBuilder:
#   - no-new-privileges, seccomp denylist (process_vm_readv/process_vm_writev/ptrace), CapDrop ALL
#   - userns remap, memory/pids limits, read-only rootfs (writable surfaces are tmpfs)
#   - the ONLY bind mount is the ext4 agent worktree at /workspace (G-11)
# and reaches the network only through the gitloom-egress-proxy (default-deny).

FROM debian:bookworm-slim

# Base tooling only: git + curl for package/dependency fetches through the proxy, plus Devbox/Nix
# so agents can `devbox add <tool>` at runtime instead of a runtime image build.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates curl git xz-utils sudo \
    && rm -rf /var/lib/apt/lists/*

# Two DISTINCT non-root users (G2 control 1): the agent CLI runs as `agent` (uid 1000); the OOB
# supervisor runs as `supervisor` (uid 1001) and solely owns /run/secrets/oob.key (mode 0400) so the
# agent uid can never read the OOB session key K.
RUN groupadd -g 1000 agent      && useradd -u 1000 -g 1000 -m -s /bin/bash agent \
    && groupadd -g 1001 supervisor && useradd -u 1001 -g 1001 -m -s /bin/bash supervisor

# Devbox (Nix under the hood) — the runtime toolchain sideload path.
RUN curl -fsSL https://get.jetify.com/devbox | bash -s -- -f \
    && chown -R agent:agent /opt 2>/dev/null || true

# /workspace is the ext4 worktree bind target; /run/secrets is a tmpfs the daemon populates post-start.
WORKDIR /workspace
USER agent

# The seccomp profile applied to this container is passed by the daemon at create time
# (HostConfig.SecurityOpt: seccomp=<json>). ./seccomp.json IS that profile and the single source of
# truth: it is embedded into GitLoom.Core and returned verbatim by SeccompProfile.Json. It is the
# canonical moby default-deny profile (defaultAction SCMP_ACT_ERRNO) with ptrace/process_vm_readv/
# process_vm_writev removed from every allow rule and explicitly denied.
CMD ["sleep", "infinity"]
