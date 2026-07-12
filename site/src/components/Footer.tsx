import { Link } from 'react-router-dom';
import { Wordmark } from './Wordmark';
import { IconGitHub } from './Icons';
import { GITHUB_URL } from '../config';

export function Footer() {
  return (
    <footer className="footer">
      <div className="container footer-inner">
        <div className="footer-brand">
          <Wordmark />
          <p className="muted" style={{ maxWidth: '32ch', marginTop: 'var(--space-3)' }}>
            A native Git client today. A control center for your coding agents tomorrow.
          </p>
          <a href={GITHUB_URL} className="footer-gh" aria-label="GitLoom on GitHub">
            <IconGitHub /> <span>dsazykin/GitLoom</span>
          </a>
        </div>
        <nav className="footer-col" aria-label="Products">
          <h4>Products</h4>
          <Link to="/client">Git Client — free</Link>
          <Link to="/pro">GitLoom Pro</Link>
          <Link to="/weave">GitLoom Weave</Link>
        </nav>
        <nav className="footer-col" aria-label="Company">
          <h4>Get in touch</h4>
          <Link to="/waitlist">Join the waitlist</Link>
          <Link to="/contact">Contact</Link>
        </nav>
      </div>
      <div className="container footer-legal">
        <span className="muted mono" style={{ fontSize: '0.75rem' }}>
          © {new Date().getFullYear()} GitLoom · rendered natively, no web view was harmed
        </span>
      </div>
    </footer>
  );
}
