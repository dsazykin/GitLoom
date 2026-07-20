# mainguard-egress-proxy

The sole route out of the internal `mainguard-agents` network (P2-07). Default-deny.

**Built in CI / the release pipeline — never at runtime** (G-16).

Three layers, all driven by the daemon-rendered allowlist:

1. **tinyproxy** — an HTTP(S) CONNECT proxy with `FilterDefaultDeny Yes` + the rendered host allow-filter.
2. **dnsmasq** — pinned DNS that answers allowlisted names only; everything else is NXDOMAIN (kills DNS
   exfiltration).
3. **iptables backstop** — DROPs any non-proxy egress, so an agent that ignores `HTTP_PROXY` and dials a
   raw IP is still dropped. Enforcing egress by proxy env alone (without this backstop) is a named
   rejection trigger.

The daemon's `EgressProxyConfigurator` renders the allowlist to `/etc/mainguard/tinyproxy-filter`,
`/etc/mainguard/dnsmasq.conf`, and `/etc/mainguard/backstop.sh` (see `EgressProxyConfig` for the exact
rendering) and calls `reload.sh`. The proxy needs `NET_ADMIN`/`NET_RAW` (added at create time) for the
backstop only.

The repo's git host is **not** on the agent allowlist (A6). Git-dependency fetches go through the
daemon read-only git proxy (`DaemonGitProxy`), which is fetch-only and refuses push structurally.
