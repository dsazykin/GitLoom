# build/libvterm — the P2-18 native terminal engine (pinned)

`build.sh` builds **libvterm 0.3.3** (pinned upstream tarball, sha256-verified — see the
constants at the top of the script) into `out/libvterm.so` for linux-x64.

- **Daemon-side only.** The `.so` rides with the published `Mainguard.Server` payload (the
  csproj bundles `out/libvterm.so` when present) and is loaded by
  `Mainguard.Agents/Terminal/Vterm/VtermNative` — resolution order: the `MAINGUARD_LIBVTERM`
  env var (explicit path), `libvterm.so(.0)` beside the daemon binary, then the system
  library. The client never ships or loads it (P2-18 invariant; the Client head's closure
  gate already excludes the whole agent platform).
- **CI** runs this script before the test step and exports `MAINGUARD_LIBVTERM` +
  `MAINGUARD_REQUIRE_LIBVTERM=1`, so the P2-04 suites run against the libvterm engine and a
  missing library is a hard failure, never a silent skip.
- **Windows local-dev** has no libvterm by design — the engine flag degrades to the interim
  engine (`TerminalEngineConfig.Resolve`) and the libvterm test legs skip locally. To run
  them from WSL, execute `build.sh` in a Linux environment (or extract Ubuntu's `libvterm0`
  package, also 0.3.3) and set `MAINGUARD_LIBVTERM`.
- **`out/` is build output** (gitignored) — nothing binary is committed here.

Changing the pinned version is a reviewed edit to `build.sh` (URL + SHA256 together) and
must re-run the full P2-04 conformance gate.
