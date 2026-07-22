#!/usr/bin/env bash
# =============================================================================
# P2-18: build the pinned libvterm as a linux-x64 shared library — DAEMON-SIDE ONLY.
# =============================================================================
# The server-side terminal engine (Mainguard.Agents/Terminal/Vterm/) P/Invokes libvterm.
# CI runs this script and hands the output to the test run (MAINGUARD_LIBVTERM) and to the
# daemon publish (Mainguard.Server bundles build/libvterm/out/libvterm.so when present —
# the client never ships or loads it; that is a P2-18 invariant).
#
# The source is PINNED: exact upstream release tarball + sha256. Changing the pin is a
# reviewed change to this file, never an ambient "latest".
#
# No libtool/perl needed: the release tarball ships the generated encoding tables, so a
# direct C compile suffices (same sources, same -std=c99 the upstream Makefile uses).
#
# Usage:  build/libvterm/build.sh [out-dir]     # default: build/libvterm/out
# Needs:  cc (gcc/clang), curl, tar, sha256sum.
set -euo pipefail

VERSION="0.3.3"
URL="https://www.leonerd.org.uk/code/libvterm/libvterm-${VERSION}.tar.gz"
SHA256="09156f43dd2128bd347cbeebe50d9a571d32c64e0cf18d211197946aff7226e0"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="${1:-$HERE/out}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "libvterm $VERSION → $OUT/libvterm.so"

TARBALL="$WORK/libvterm-$VERSION.tar.gz"
curl -fsSL --retry 3 -o "$TARBALL" "$URL"
echo "$SHA256  $TARBALL" | sha256sum -c - >/dev/null

tar -xzf "$TARBALL" -C "$WORK"
SRC="$WORK/libvterm-$VERSION"

CC="${CC:-cc}"
mkdir -p "$OUT"
"$CC" -O2 -fPIC -shared -std=c99 -Wall \
  -Wl,-soname,libvterm.so.0 \
  -I"$SRC/include" \
  "$SRC"/src/*.c \
  -o "$OUT/libvterm.so"

sha256sum "$OUT/libvterm.so" | tee "$OUT/libvterm.so.sha256"
echo "OK: $(file "$OUT/libvterm.so" 2>/dev/null || echo built)"
