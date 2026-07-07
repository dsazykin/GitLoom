import { Link } from 'react-router-dom';
import { Reveal } from '../lib/Reveal';
import { GraphVignette, StagingVignette, WindowFrame } from '../components/Vignettes';
import { ThemeSwitcher } from '../components/ThemeSwitcher';
import { IconGraph, IconStage, IconMerge, IconWorktree, IconNoLock, IconZap } from '../components/Icons';

export function Client() {
  return (
    <>
      <div className="container page-hero">
        <span className="pill" style={{ borderColor: 'var(--success)', color: 'var(--success)' }}>
          Free forever · no account, ever
        </span>
        <h1 style={{ marginTop: 'var(--space-4)' }}>The Git client, taken seriously again.</h1>
        <p className="lede">
          Most Git GUIs are a web page in a window: slow to open, slow to scroll, and account-walled.
          GitLoom is rendered natively — a precision instrument for the work you do a hundred times a
          day.
        </p>
        <hr className="thread-rule" />
      </div>

      <section className="section" aria-label="What it fixes">
        <div className="container">
          <Reveal>
            <h2>What it fixes</h2>
          </Reveal>
          <Reveal delay={80}>
            <div className="ledger" role="img" aria-label="The problems with existing Git clients">
              <span className="err">✗</span> <span className="dim">Electron clients that take 200MB of RAM to show a diff</span>
              <br />
              <span className="err">✗</span> <span className="dim">"Free" tiers that demand a login and block private repos</span>
              <br />
              <span className="err">✗</span> <span className="dim">.git/index.lock corruption when two tools touch one repo</span>
              <br />
              <span className="err">✗</span> <span className="dim">Destructive operations one mis-click away from lost work</span>
              <br />
              <br />
              <span className="ok">✓</span> native rendering, instant startup, 60fps everywhere
              <br />
              <span className="ok">✓</span> the full client is free — no login, no telemetry, no held-hostage features
              <br />
              <span className="ok">✓</span> deterministic repository access: open, operate, release. No lock collisions.
              <br />
              <span className="ok">✓</span> the safe path is always the default path
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="Features">
        <div className="container">
          <Reveal>
            <div className="feature-row">
              <div>
                <h3>
                  <IconGraph /> A commit graph that moves like the app is native — because it is
                </h3>
                <p className="muted">
                  Branches, merges and tags drawn as colored lanes at a rock-solid 60fps, whether
                  your history has a hundred commits or a hundred thousand. Scrolling never stutters;
                  selection never lags.
                </p>
              </div>
              <GraphVignette />
            </div>
          </Reveal>

          <Reveal>
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconStage /> Stage exactly what you mean
                </h3>
                <p className="muted">
                  Files, hunks, or single lines — build precise commits from messy working
                  directories. The diff view shows whitespace ghosts and word-level changes so
                  nothing slips through.
                </p>
              </div>
              <StagingVignette />
            </div>
          </Reveal>

          <Reveal>
            <div className="feature-row">
              <div>
                <h3>
                  <IconMerge /> Conflicts, without the cold sweat
                </h3>
                <p className="muted">
                  Ours, theirs, and the merged result side by side. Every choice is reversible until
                  you commit, and the UI always makes the safer path the obvious one — that's the
                  house rule.
                </p>
              </div>
              <WindowFrame title="gitloom — resolve · Router.cs">
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
                  <div style={{ background: 'var(--diff-added-bg)', borderRadius: 'var(--radius-sm)', padding: '10px 12px' }}>
                    <span className="mono" style={{ fontSize: 11, color: 'var(--success)' }}>OURS · main</span>
                    <div style={{ height: 6, width: '85%', background: 'var(--text-muted)', opacity: 0.4, borderRadius: 3, marginTop: 8 }} />
                    <div style={{ height: 6, width: '60%', background: 'var(--text-muted)', opacity: 0.4, borderRadius: 3, marginTop: 6 }} />
                  </div>
                  <div style={{ background: 'var(--diff-removed-bg)', borderRadius: 'var(--radius-sm)', padding: '10px 12px' }}>
                    <span className="mono" style={{ fontSize: 11, color: 'var(--danger)' }}>THEIRS · feature</span>
                    <div style={{ height: 6, width: '70%', background: 'var(--text-muted)', opacity: 0.4, borderRadius: 3, marginTop: 8 }} />
                    <div style={{ height: 6, width: '90%', background: 'var(--text-muted)', opacity: 0.4, borderRadius: 3, marginTop: 6 }} />
                  </div>
                </div>
                <div style={{ border: '1px solid var(--accent)', background: 'var(--accent-selection)', borderRadius: 'var(--radius-sm)', padding: '10px 12px', marginTop: 10 }}>
                  <span className="mono" style={{ fontSize: 11, color: 'var(--accent)' }}>RESULT — take ours, then theirs</span>
                </div>
              </WindowFrame>
            </div>
          </Reveal>

          <Reveal>
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconWorktree /> Branches, tags, stashes, worktrees
                </h3>
                <p className="muted">
                  Full management of everything your repository holds, in one window. Worktrees are
                  first-class — the same foundation that will host agent sandboxes in Pro.
                </p>
              </div>
              <WindowFrame title="gitloom — refs">
                <div style={{ display: 'grid', gap: 8 }}>
                  {[
                    ['main', 'var(--lane-1)', 'worktree · ~/code/app'],
                    ['feature/import-wizard', 'var(--lane-2)', 'worktree · ~/code/app-wt1'],
                    ['fix/flaky-tests', 'var(--lane-3)', '2 ahead'],
                    ['v2.4.0', 'var(--lane-4)', 'tag'],
                  ].map(([name, color, note]) => (
                    <div key={name} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '8px 10px', borderRadius: 'var(--radius-sm)', background: 'var(--surface-card)', border: '1px solid var(--border-hairline)' }}>
                      <span className="mono" style={{ fontSize: 12.5, color: 'var(--text-primary)', display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                        <span style={{ width: 8, height: 8, borderRadius: '50%', background: color as string, display: 'inline-block' }} />
                        {name}
                      </span>
                      <span className="mono" style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>{note}</span>
                    </div>
                  ))}
                </div>
              </WindowFrame>
            </div>
          </Reveal>

          <Reveal>
            <div className="feature-row">
              <div>
                <h3>
                  <IconZap /> Five themes, one design system
                </h3>
                <p className="muted">
                  Midnight Loom, Daylight Loom, Command Deck, Atelier, Loom Aurora. Every color in
                  the app is a named token — switch palettes live, and every surface follows. This
                  website runs on the same system; try it.
                </p>
                <ThemeSwitcher large />
              </div>
              <WindowFrame title="gitloom — settings · appearance">
                <div style={{ display: 'grid', gap: 10 }}>
                  {['Midnight Loom', 'Daylight Loom', 'Command Deck', 'Atelier', 'Loom Aurora'].map((t, i) => (
                    <div key={t} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '7px 10px', borderRadius: 'var(--radius-sm)', border: i === 0 ? '1px solid var(--accent)' : '1px solid var(--border-hairline)', background: i === 0 ? 'var(--accent-selection)' : 'transparent' }}>
                      <span style={{ width: 14, height: 14, borderRadius: 4, background: `var(--lane-${i + 1})` }} />
                      <span style={{ fontSize: 13, color: 'var(--text-primary)' }}>{t}</span>
                    </div>
                  ))}
                </div>
              </WindowFrame>
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="Why it is free">
        <div className="container">
          <Reveal>
            <h2>Why free? Honestly:</h2>
            <p className="lede">
              The client is our handshake. It's complete — nothing is held hostage behind an
              upgrade prompt, and it never asks who you are.{' '}
              <IconNoLock style={{ verticalAlign: '-4px' }} /> When you're ready to put coding
              agents to work, <Link to="/pro">Pro</Link> is where GitLoom earns its keep. Until
              then, enjoy a Git client that respects you.
            </p>
          </Reveal>
        </div>
      </section>

      <section className="cta-band" aria-label="Call to action">
        <div className="container">
          <Reveal>
            <h2>Shipping this fall.</h2>
            <p className="muted" style={{ marginInline: 'auto' }}>
              Join the waitlist and get the download link the moment it's live.
            </p>
            <Link to="/waitlist?p=client" className="btn btn-accent btn-lg">
              Get the client first
            </Link>
          </Reveal>
        </div>
      </section>
    </>
  );
}
