import { Link } from 'react-router-dom';
import { Reveal } from '../lib/Reveal';
import { WeaveCloudVignette } from '../components/Vignettes';
import { IconCloud, IconShield, IconThreads } from '../components/Icons';

export function Weave() {
  return (
    <>
      <div className="container page-hero">
        <span className="pill pill-accent">Paid · cloud · waitlist open</span>
        <h1 style={{ marginTop: 'var(--space-4)' }}>Describe it. Watch it woven.</h1>
        <p className="lede">
          GitLoom Weave is for builders who don't want to think about git at all. Tell it what you
          want; coding agents build it in the cloud, every change is verified before it counts, and
          you get a working product — plus a clean, professional history underneath, in case you
          ever need it.
        </p>
        <hr className="thread-rule" />
      </div>

      <section className="section" aria-label="The problem">
        <div className="container">
          <Reveal>
            <h2>Vibe coding has a trust problem.</h2>
            <p className="lede">
              AI builders will happily generate an app for you. But when something breaks — and
              something always breaks — you're left holding ten thousand lines nobody checked, with
              no record of what changed or why. Weave keeps the magic and adds the safety net.
            </p>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="How it works">
        <div className="container">
          <Reveal>
            <div className="feature-row">
              <div>
                <h2 style={{ marginBottom: 'var(--space-8)' }}>How it works</h2>
                <div className="steps">
                  <div className="step">
                    <div>
                      <h3>Say what you want</h3>
                      <p>
                        In plain language. A booking page, an internal tool, a storefront — no
                        setup, no repositories, no jargon.
                      </p>
                    </div>
                  </div>
                  <div className="step">
                    <div>
                      <h3>Agents weave it in the cloud</h3>
                      <p>
                        Several agents work in parallel in isolated cloud sandboxes, each on its own
                        thread of the work. Nothing runs on your machine.
                      </p>
                    </div>
                  </div>
                  <div className="step">
                    <div>
                      <h3>Only verified work reaches you</h3>
                      <p>
                        Every thread passes automated checks before it's woven into your product.
                        You preview the result, not the chaos behind it.
                      </p>
                    </div>
                  </div>
                </div>
              </div>
              <WeaveCloudVignette />
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="What you get">
        <div className="container">
          <Reveal>
            <h2>What you keep</h2>
            <div style={{ display: 'grid', gap: 'var(--space-6)', maxWidth: '46rem', marginTop: 'var(--space-8)' }}>
              <p>
                <IconCloud style={{ verticalAlign: '-4px', color: 'var(--accent)' }} />{' '}
                <strong>A live product, not a code dump.</strong>{' '}
                <span className="muted">
                  Weave hosts what it builds. Iterate by asking; ship by clicking.
                </span>
              </p>
              <p>
                <IconShield style={{ verticalAlign: '-4px', color: 'var(--accent)' }} />{' '}
                <strong>Verified changes, always.</strong>{' '}
                <span className="muted">
                  The same deterministic gates that power GitLoom Pro run behind every request you
                  make — tests pass or it doesn't land.
                </span>
              </p>
              <p>
                <IconThreads style={{ verticalAlign: '-4px', color: 'var(--accent)' }} />{' '}
                <strong>A real history, for the day you need it.</strong>{' '}
                <span className="muted">
                  Everything is proper git underneath: clean commits, full provenance. Hand it to a
                  developer — or to GitLoom Pro — and nothing is a black box.
                </span>
              </p>
            </div>
          </Reveal>
        </div>
      </section>

      <section className="section" aria-label="Who it is for">
        <div className="container">
          <Reveal>
            <h2>Made for the founders, not the git logs.</h2>
            <p className="lede">
              Non-technical founders, designers, operators, tinkerers — anyone with something to
              build and no appetite for merge conflicts. If you outgrow it, your project graduates
              to Pro with its history intact. Nothing to migrate, nothing to untangle.
            </p>
          </Reveal>
        </div>
      </section>

      <section className="cta-band" aria-label="Call to action">
        <div className="container">
          <Reveal>
            <h2>Weave arrives after Pro.</h2>
            <p className="muted" style={{ marginInline: 'auto' }}>
              Paid, cloud-based, pricing announced at launch. The waitlist gets first invites.
            </p>
            <Link to="/waitlist?p=weave" className="btn btn-accent btn-lg">
              Join the Weave waitlist
            </Link>
          </Reveal>
        </div>
      </section>
    </>
  );
}
