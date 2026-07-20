# Mainguard egress proxy image (P2-07).
#
# Built in CI / the release pipeline ONLY — never at runtime (G-16). This is the sole route out of the
# internal `mainguard-agents` network: an allowlist-driven HTTP(S) CONNECT proxy (tinyproxy) + pinned DNS
# (dnsmasq answering allowlisted names only, NXDOMAIN otherwise — kills DNS exfiltration) + an iptables
# backstop that DROPs non-proxy egress (proxy-env-only enforcement is a rejection trigger).
#
# The daemon (EgressProxyConfigurator) renders the allowlist to the three config artefacts at runtime
# and calls /etc/mainguard/reload.sh:
#   /etc/mainguard/tinyproxy-filter  — the CONNECT allow-filter (default-deny)
#   /etc/mainguard/dnsmasq.conf      — pinned DNS (allowlisted names only)
#   /etc/mainguard/backstop.sh       — iptables DROP for non-proxy egress

FROM debian:bookworm-slim

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        tinyproxy dnsmasq iptables ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /etc/mainguard
COPY reload.sh /etc/mainguard/reload.sh
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /etc/mainguard/reload.sh /entrypoint.sh

# NET_ADMIN/NET_RAW (added by the daemon at create time) are required for the iptables backstop only.
ENTRYPOINT ["/entrypoint.sh"]
