import type { ReactNode } from 'react';

/**
 * Product vignettes: the app's surfaces rebuilt in CSS/SVG so they re-skin
 * with the live theme — imagery that is also a design-system demo.
 */

export function WindowFrame({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="window">
      <div className="window-bar">
        <span className="window-dot" />
        <span className="window-dot" />
        <span className="window-dot" />
        <span className="window-title">{title}</span>
      </div>
      <div className="window-body">{children}</div>
    </div>
  );
}

/** Commit graph with three weaving lanes and ref chips. */
export function GraphVignette() {
  return (
    <WindowFrame title="gitloom — commit graph">
      <svg viewBox="0 0 420 190" width="100%" role="img" aria-label="Commit graph with three branch lanes merging">
        <g fill="none" strokeWidth="2">
          <path d="M30 20v150" stroke="var(--lane-1)" />
          <path d="M30 45c0 25 40 15 40 40v45c0 25-40 15-40 40" stroke="var(--lane-2)" />
          <path d="M30 25c0 30 80 20 80 50v30c0 30-80 20-80 50" stroke="var(--lane-3)" />
        </g>
        {[20, 60, 100, 140, 170].map((y) => (
          <circle key={y} cx="30" cy={y} r="5" fill="var(--surface-panel)" stroke="var(--lane-1)" strokeWidth="2" />
        ))}
        <circle cx="70" cy="95" r="5" fill="var(--surface-panel)" stroke="var(--lane-2)" strokeWidth="2" />
        <circle cx="110" cy="90" r="5" fill="var(--surface-panel)" stroke="var(--lane-3)" strokeWidth="2" />
        <g fontFamily="var(--font-mono)" fontSize="10.5">
          <rect x="130" y="82" rx="8" width="88" height="17" fill="var(--accent-selection)" stroke="var(--accent)" strokeWidth="1" />
          <text x="141" y="94" fill="var(--accent)">agent/parser</text>
          <rect x="46" y="12" rx="8" width="44" height="17" fill="var(--accent-selection)" stroke="var(--accent)" strokeWidth="1" />
          <text x="57" y="24" fill="var(--accent)">main</text>
        </g>
        <g fontFamily="var(--font-sans)" fontSize="11" fill="var(--text-muted)">
          <text x="240" y="24">refactor commit router</text>
          <text x="240" y="64">stage hunks by selection</text>
          <text x="240" y="95" fill="var(--text-primary)">merge agent/parser — verified ✓</text>
          <text x="240" y="144">initial worktree layout</text>
        </g>
      </svg>
    </WindowFrame>
  );
}

/** Partial staging: diff lines with per-hunk stage control. */
export function StagingVignette() {
  const lines: Array<['add' | 'del' | 'ctx', number]> = [
    ['ctx', 62],
    ['del', 48],
    ['add', 55],
    ['add', 34],
    ['ctx', 70],
    ['add', 42],
    ['ctx', 28],
  ];
  return (
    <WindowFrame title="gitloom — staging · Parser.cs">
      <div style={{ display: 'grid', gap: 6 }}>
        {lines.map(([kind, w], i) => (
          <div
            key={i}
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: 10,
              padding: '4px 8px',
              borderRadius: 'var(--radius-sm)',
              background:
                kind === 'add'
                  ? 'var(--diff-added-bg)'
                  : kind === 'del'
                    ? 'var(--diff-removed-bg)'
                    : 'transparent',
            }}
          >
            <span
              className="mono"
              style={{
                fontSize: 11,
                width: 12,
                color: kind === 'add' ? 'var(--success)' : kind === 'del' ? 'var(--danger)' : 'var(--text-muted)',
              }}
            >
              {kind === 'add' ? '+' : kind === 'del' ? '−' : ' '}
            </span>
            <span
              style={{
                height: 7,
                width: `${w}%`,
                borderRadius: 4,
                background: kind === 'ctx' ? 'var(--surface-hover)' : 'var(--text-muted)',
                opacity: kind === 'ctx' ? 1 : 0.55,
              }}
            />
          </div>
        ))}
        <div style={{ display: 'flex', gap: 8, marginTop: 6 }}>
          <span className="pill pill-accent">Stage hunk</span>
          <span className="pill">Stage line</span>
          <span className="pill">Discard…</span>
        </div>
      </div>
    </WindowFrame>
  );
}

/** Pro: parallel agents in sandboxed worktrees with verification states. */
export function AgentsVignette() {
  const rows = [
    { name: 'claude-code · fix/flaky-tests', state: 'Verified', color: 'var(--success)', pct: 100 },
    { name: 'agy · feat/import-wizard', state: 'Running tests', color: 'var(--warning)', pct: 64 },
    { name: 'opencode · refactor/db-layer', state: 'Writing', color: 'var(--info)', pct: 31 },
  ];
  return (
    <WindowFrame title="gitloom pro — agents">
      <div style={{ display: 'grid', gap: 12 }}>
        {rows.map((r) => (
          <div
            key={r.name}
            style={{
              display: 'grid',
              gap: 8,
              padding: '10px 12px',
              borderRadius: 'var(--radius-md)',
              border: '1px solid var(--border-hairline)',
              background: 'var(--surface-card)',
            }}
          >
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8 }}>
              <span className="mono" style={{ fontSize: 12, color: 'var(--text-primary)' }}>
                {r.name}
              </span>
              <span
                className="pill"
                style={{ borderColor: r.color, color: r.color, background: 'transparent', flexShrink: 0 }}
              >
                {r.state}
              </span>
            </div>
            <div style={{ height: 5, borderRadius: 4, background: 'var(--surface-hover)' }}>
              <div style={{ height: 5, width: `${r.pct}%`, borderRadius: 4, background: r.color }} />
            </div>
          </div>
        ))}
        <p className="mono" style={{ fontSize: 11.5, color: 'var(--text-muted)', margin: 0 }}>
          3 worktrees · 0 lock collisions · your working directory untouched
        </p>
      </div>
    </WindowFrame>
  );
}

/** Pro: the verification pipeline gate. */
export function PipelineVignette() {
  const stages = [
    { label: 'build', ok: true },
    { label: 'tests 214/214', ok: true },
    { label: 'lint', ok: true },
    { label: 'review', ok: false },
  ];
  return (
    <WindowFrame title="gitloom pro — verify & merge">
      <div style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 8 }}>
        {stages.map((s, i) => (
          <span key={s.label} style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
            <span
              className="pill"
              style={
                s.ok
                  ? { borderColor: 'var(--success)', color: 'var(--success)', background: 'var(--diff-added-bg)' }
                  : { borderColor: 'var(--accent)', color: 'var(--accent)', background: 'var(--accent-selection)' }
              }
            >
              {s.ok ? '✓ ' : '● '}
              {s.label}
            </span>
            {i < stages.length - 1 && <span style={{ color: 'var(--text-muted)' }}>→</span>}
          </span>
        ))}
      </div>
      <p className="mono" style={{ fontSize: 11.5, color: 'var(--text-muted)', margin: '14px 0 0' }}>
        merge blocked until every gate passes — nothing lands unverified
      </p>
    </WindowFrame>
  );
}

/** Weave: prompt in, woven product out. */
export function WeaveCloudVignette() {
  return (
    <WindowFrame title="gitloom weave — new thread">
      <div
        style={{
          border: '1px solid var(--border-hairline)',
          borderRadius: 'var(--radius-md)',
          padding: '12px 14px',
          background: 'var(--surface-card)',
          fontSize: 14,
          color: 'var(--text-primary)',
        }}
      >
        “A booking page for my pottery studio — calendar, deposits, email reminders.”
      </div>
      <svg viewBox="0 0 420 70" width="100%" aria-hidden style={{ display: 'block', margin: '6px 0' }}>
        <g fill="none" strokeWidth="1.8">
          <path d="M60 6 C 130 6, 150 35, 210 35 S 300 64, 360 64" stroke="var(--lane-1)" />
          <path d="M210 6 C 250 6, 260 35, 210 35 S 160 64, 210 64" stroke="var(--lane-2)" />
          <path d="M360 6 C 290 6, 270 35, 210 35 S 120 64, 60 64" stroke="var(--lane-3)" />
        </g>
      </svg>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <span className="pill" style={{ borderColor: 'var(--success)', color: 'var(--success)' }}>
          ✓ 3 agents finished
        </span>
        <span className="pill" style={{ borderColor: 'var(--success)', color: 'var(--success)' }}>
          ✓ checks passed
        </span>
        <span className="pill pill-accent">Preview your site →</span>
      </div>
    </WindowFrame>
  );
}
