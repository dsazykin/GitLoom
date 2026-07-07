import { useEffect, useState } from 'react';
import { Link, NavLink, useLocation } from 'react-router-dom';
import { Wordmark } from './Wordmark';
import { ThemeSwitcher } from './ThemeSwitcher';

const LINKS = [
  { to: '/client', label: 'Git Client', tag: 'Free' },
  { to: '/pro', label: 'Pro', tag: null },
  { to: '/weave', label: 'Weave', tag: null },
  { to: '/contact', label: 'Contact', tag: null },
];

export function Nav() {
  const [open, setOpen] = useState(false);
  const location = useLocation();

  useEffect(() => setOpen(false), [location.pathname]);

  useEffect(() => {
    document.body.style.overflow = open ? 'hidden' : '';
    return () => {
      document.body.style.overflow = '';
    };
  }, [open]);

  return (
    <header className="nav">
      <div className="container nav-inner">
        <Link to="/" className="nav-brand" aria-label="GitLoom home">
          <Wordmark />
        </Link>

        <nav className="nav-links" aria-label="Primary">
          {LINKS.map((l) => (
            <NavLink key={l.to} to={l.to} className="nav-link">
              {l.label}
              {l.tag && <span className="nav-tag">{l.tag}</span>}
            </NavLink>
          ))}
        </nav>

        <div className="nav-actions">
          <ThemeSwitcher />
          <Link to="/waitlist" className="btn btn-accent nav-cta">
            Join waitlist
          </Link>
          <button
            className="nav-burger"
            aria-expanded={open}
            aria-label={open ? 'Close menu' : 'Open menu'}
            onClick={() => setOpen((o) => !o)}
          >
            <svg width="22" height="22" viewBox="0 0 22 22" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" fill="none" aria-hidden>
              {open ? <path d="M5 5l12 12M17 5L5 17" /> : <path d="M3.5 6.5h15M3.5 11h15M3.5 15.5h15" />}
            </svg>
          </button>
        </div>
      </div>

      {open && (
        <nav className="nav-sheet" aria-label="Primary mobile">
          {LINKS.map((l) => (
            <NavLink key={l.to} to={l.to} className="nav-sheet-link">
              {l.label}
              {l.tag && <span className="nav-tag">{l.tag}</span>}
            </NavLink>
          ))}
          <Link to="/waitlist" className="btn btn-accent btn-lg" style={{ justifyContent: 'center' }}>
            Join waitlist
          </Link>
          <div style={{ paddingTop: 'var(--space-4)' }}>
            <ThemeSwitcher large />
          </div>
        </nav>
      )}
    </header>
  );
}
