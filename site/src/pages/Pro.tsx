import { Link } from 'react-router-dom';
import { Reveal } from '../lib/Reveal';
import { AgentsVignette, PipelineVignette, WindowFrame } from '../components/Vignettes';
import { IconShield, IconEye, IconKey, IconWorktree } from '../components/Icons';

export function Pro() {
  return (
    <>
      <div className="container page-hero">
        <span className="pill pill-accent">Paid · waitlist open</span>
        <h1 style={{ marginTop: 'var(--space-4)' }}>Run a swarm. Keep control.</h1>
        <p className="lede">
          Coding agents are fast. Unsupervised, they're also reckless — they overwrite each other,
          break your working directory, and ship code nobody verified. GitLoom Pro turns one
          repository into a safe runway for many agents, with you in the tower.
        </p>
        <hr className="thread-rule" />
      </div>

      <section className="section" aria-label="The problem">
        <div className="container">
          <Reveal>
            <h2>The bottleneck moved.</h2>
            <p className="lede">
              Generating code is cheap now. The expensive part is what happens after: checking it,
              trusting it, merging it. Today that pipeline is you, alt-tabbing between terminals,
              praying nothing collides.
            </p>
          </Reveal>
          <Reveal delay={80}>
            <div className="ledger" role="img" aria-label="What goes wrong running multiple agents against one repository">
              <span className="dim">Three agents, one repo, no GitLoom:</span>
              <br />
              <span className="err">✗ agent-2 committed over agent-1's half-finished refactor</span>
              <br />
              <span className="err">✗ agent-3 force-pushed and ate your local fixes</span>
              <br />
              <span className="err">✗ 4,000 generated lines merged — tests never ran</span>
              <br />
              <br />
              <span className="dim">Three agents, one repo, GitLoom Pro:</span>
              <br />
              <span className="ok">✓ each agent works a sandboxed worktree — collisions are impossible</span>
              <br />
              <span className="ok">✓ every change passes build + tests + lint before it can merge</span>
              <br />
              <span className="ok">✓ you review verified diffs from one cockpit, then land them</span>
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
                  <IconWorktree /> Sandboxed parallel agents
                </h3>
                <p className="muted">
                  Launch Claude Code, AGY, OpenCode — any CLI agent — each in its own isolated
                  worktree. They can't touch each other's work, and they can never touch yours. The
                  index.lock era is over.
                </p>
              </div>
              <AgentsVignette />
            </div>
          </Reveal>

          <Reveal>
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconShield /> The verification pipeline
                </h3>
                <p className="muted">
                  Nothing merges on vibes. Every agent change runs your build, your tests, your
                  linters — deterministic gates, not a model's opinion of its own work. Green means
                  verified; red never lands.
                </p>
              </div>
              <PipelineVignette />
            </div>
          </Reveal>

          <Reveal>
            <div className="feature-row">
              <div>
                <h3>
                  <IconEye /> The review cockpit
                </h3>
                <p className="muted">
                  All agent output flows into one queue of verified diffs. Approve, request changes,
                  or drop — with full provenance of who (or what) wrote every hunk. Supervise a
                  swarm without losing the thread.
                </p>
              </div>
              <WindowFrame title="gitloom pro — review queue">
                <div style={{ display: 'grid', gap: 8 }}>
                  {[
                    ['fix/flaky-tests', '+64 −12', 'verified ✓', 'var(--success)'],
                    ['feat/import-wizard', '+412 −38', 'verified ✓', 'var(--success)'],
                    ['refactor/db-layer', '+840 −790', 'running…', 'var(--warning)'],
                  ].map(([branch, delta, state, color]) => (
                    <div key={branch as string} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 8, padding: '9px 11px', borderRadius: 'var(--radius-sm)', border: '1px solid var(--border-hairline)', background: 'var(--surface-card)' }}>
                      <span className="mono" style={{ fontSize: 12, color: 'var(--text-primary)' }}>{branch}</span>
                      <span className="mono" style={{ fontSize: 11, color: 'var(--text-muted)' }}>{delta}</span>
                      <span className="mono" style={{ fontSize: 11, color: color as string }}>{state}</span>
                    </div>
                  ))}
                </div>
              </WindowFrame>
            </div>
          </Reveal>

          <Reveal>
            <div className="feature-row flip">
              <div>
                <h3>
                  <IconKey /> Your keys, your models, your terms
                </h3>
                <p className="muted">
                  Bring your own API keys or existing agent subscriptions — Pro orchestrates, it
                  doesn't meter your tokens. A built-in gateway smooths rate limits across agents so
                  a burst from one doesn't starve the rest.
                </p>
              </div>
              <WindowFrame title="gitloom pro — gateway">
                <div style={{ display: 'grid', gap: 8 }}>
                  {[
                    ['anthropic · your key', '38 rpm', 'var(--lane-1)'],
                    ['openai · your key', '12 rpm', 'var(--lane-5)'],
                    ['local · ollama', 'unmetered', 'var(--lane-3)'],
                  ].map(([provider, rate, color]) => (
                    <div key={provider as string} style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '9px 11px', borderRadius: 'var(--radius-sm)', border: '1px solid var(--border-hairline)', background: 'var(--surface-card)' }}>
                      <span className="mono" style={{ fontSize: 12, color: 'var(--text-primary)', display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                        <span style={{ width: 8, height: 8, borderRadius: '50%', background: color as string }} />
                        {provider}
                      </span>
                      <span className="mono" style={{ fontSize: 11, color: 'var(--text-muted)' }}>{rate}</span>
                    </div>
                  ))}
                </div>
              </WindowFrame>
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="How Pro compares">
        <div className="container">
          <Reveal>
            <h2>Why not just use…</h2>
            <div style={{ display: 'grid', gap: 'var(--space-4)', maxWidth: '46rem', marginTop: 'var(--space-8)' }}>
              <p>
                <strong>…a vendor's own agent app?</strong>{' '}
                <span className="muted">
                  They orchestrate one vendor's agent. Pro is neutral ground — mix agents, swap
                  them as models leapfrog each other, keep one workflow.
                </span>
              </p>
              <p>
                <strong>…an orchestrator GUI?</strong>{' '}
                <span className="muted">
                  Most launch agents and hope. Almost none verify output with deterministic gates,
                  and few treat your working directory as sacred. Verification is the product here,
                  not an afterthought.
                </span>
              </p>
              <p>
                <strong>…the terminal, like today?</strong>{' '}
                <span className="muted">
                  You can babysit two agents in tmux. You can't babysit five. The ceiling on your
                  leverage is the pipeline, and Pro is that pipeline.
                </span>
              </p>
            </div>
          </Reveal>
        </div>
      </section>

      <section className="cta-band" aria-label="Call to action">
        <div className="container">
          <Reveal>
            <h2>Pro launches after the free client.</h2>
            <p className="muted" style={{ marginInline: 'auto' }}>
              Paid, with pricing announced at launch. Waitlist members get early access and founding
              terms.
            </p>
            <Link to="/waitlist?p=pro" className="btn btn-accent btn-lg">
              Join the Pro waitlist
            </Link>
          </Reveal>
        </div>
      </section>
    </>
  );
}
