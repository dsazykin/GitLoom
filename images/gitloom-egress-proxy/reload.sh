#!/bin/sh
# Applies the daemon-rendered egress policy (P2-07). Called by the entrypoint and on every config push.
# The three artefacts are rendered by EgressProxyConfigurator/EgressProxyConfig from the allowlist.
set -eu

CONF_DIR=/etc/gitloom
PROXY_PORT=8888

# 1. Pinned DNS: dnsmasq answers allowlisted names only; everything else NXDOMAIN (kills DNS exfil).
if [ -f "$CONF_DIR/dnsmasq.conf" ]; then
    pkill dnsmasq 2>/dev/null || true
    # Detach the daemon's stdin/stdout/stderr from the caller's pipe. When reload.sh is invoked
    # over a Docker exec, a backgrounded child that inherits the exec's stdout keeps the attach
    # stream open forever, so ReadOutputToEnd on the daemon side never sees EOF (the setup hangs).
    dnsmasq --conf-file="$CONF_DIR/dnsmasq.conf" --keep-in-foreground </dev/null >/dev/null 2>&1 &
fi

# 2. HTTP(S) CONNECT allowlist: tinyproxy with FilterDefaultDeny + the rendered host filter.
if [ -f "$CONF_DIR/tinyproxy-filter" ]; then
    cat > /etc/tinyproxy/tinyproxy.conf <<EOF
Port $PROXY_PORT
Listen 0.0.0.0
Timeout 600
Filter "$CONF_DIR/tinyproxy-filter"
FilterDefaultDeny Yes
FilterExtended On
ConnectPort 443
ConnectPort 80
EOF
    pkill tinyproxy 2>/dev/null || true
    # tinyproxy daemonizes (forks + parent exits); redirect its fds too so the daemon child never
    # holds the exec's attach pipe open.
    tinyproxy -c /etc/tinyproxy/tinyproxy.conf </dev/null >/dev/null 2>&1
fi

# 3. iptables backstop: DROP non-proxy egress (the control that makes direct-IP egress fail — NOT
#    proxy-env alone, which is a rejection trigger).
if [ -f "$CONF_DIR/backstop.sh" ]; then
    sh "$CONF_DIR/backstop.sh"
fi
