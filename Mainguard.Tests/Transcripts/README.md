# P2-04 golden transcripts

Each `<name>.bytes` is a raw terminal output stream; each `<name>.golden` is the deterministic,
cell-by-cell serialization of the grid the interim engine produces when the bytes are replayed
(see `../Terminal/TranscriptReplayTests.cs`). Replay is **byte-order-only** — there are no
timestamps in the fixtures and none are read.

| Transcript | Source | Notes |
|---|---|---|
| `vim.bytes` | representative | alt-screen, line-number gutter, syntax color, reverse-video status line |
| `htop-60s.bytes` | representative | colored CPU/mem meters + process rows across several refresh frames (stands in for 60 s of updates) |
| `tmux.bytes` | representative | two panes, a colored divider column, a status bar |
| `claude-code.bytes` | **synthetic** | Claude Code is a proprietary AI CLI, not installable in this environment; this stream is hand-crafted to exercise color + cursor motion + a spinner redraw |
| `opencode.bytes` | **synthetic** | OpenCode is a proprietary AI CLI, not available here; hand-crafted color + cursor stream |

## Regenerating

The `.bytes` fixtures are committed and replayed verbatim, so replay is deterministic regardless of
what is installed at replay time. To (re)capture the real-program streams through a PTY, run the
gated recorder inside the Linux container:

```bash
docker compose run --rm shell
apt-get update && apt-get install -y vim htop tmux
MAINGUARD_RECORD_TRANSCRIPTS=1 dotnet test Mainguard.slnx --filter FullyQualifiedName~RecordRealTranscripts
```

To regenerate the `.golden` files after an intentional engine change (the determinism test fails
otherwise, by design):

```bash
MAINGUARD_REGEN_GOLDENS=1 dotnet test Mainguard.slnx --filter FullyQualifiedName~TranscriptReplay
```

`.bytes` files are marked binary in `.gitattributes` (never EOL-normalized); `.golden` files are
LF-locked text so they diff cleanly.
