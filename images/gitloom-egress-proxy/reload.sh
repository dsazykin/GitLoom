#!/bin/sh
# Applies the daemon-rendered egress policy (P2-07). Called by the entrypoint and on every config push.
# The three artefacts are rendered by EgressProxyConfigurator/EgressProxyConfig from the allowlist.
set -eu

CONF_DIR=/etc/gitloom
PROXY_PORT=8888

# 1. Pinned DNS: dnsmasq answers allowlisted names only; everything else NXDOMAIN (kills DNS exfil).
if [ -f "$CONF_DIR/dnsmasq.conf" ]; then
    pkill dnsmasq 2>/dev/null || true
    dnsmasq --conf-file="$CONF_DIR/dnsmasq.conf" --keep-in-foreground &
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
    tinyproxy -c /etc/tinyproxy/tinyproxy.conf
fi

# 3. iptables backstop: DROP non-proxy egress (the control that makes direct-IP egress fail — NOT
#    proxy-env alone, which is a rejection trigger).
if [ -f "$CONF_DIR/backstop.sh" ]; then
    sh "$CONF_DIR/backstop.sh"
fi
