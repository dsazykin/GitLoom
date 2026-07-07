import { useState, type FormEvent } from 'react';
import { Turnstile } from '../components/Turnstile';
import { postJson } from '../lib/api';

const TOPICS = ['General', 'Early access & beta', 'Partnerships', 'Press', 'Support'];

export function Contact() {
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [topic, setTopic] = useState(TOPICS[0]);
  const [message, setMessage] = useState('');
  const [token, setToken] = useState<string | null>(null);
  const [hp, setHp] = useState('');
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    if (!name.trim()) return setError('Please enter your name.');
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(email))
      return setError('Please enter a valid email address.');
    if (!message.trim()) return setError('Please write a message.');
    if (message.length > 5000) return setError('Message is too long (max 5000 characters).');
    if (!token) return setError('Please complete the anti-spam check below.');

    setBusy(true);
    const res = await postJson('/api/contact', {
      name,
      email,
      topic,
      message,
      turnstileToken: token,
      website: hp,
    });
    setBusy(false);
    if (res.ok) setDone(true);
    else setError(res.error ?? 'Something went wrong. Please try again.');
  }

  if (done) {
    return (
      <div className="container form-page">
        <div className="success-panel" role="status">
          <svg className="success-check" width="56" height="56" viewBox="0 0 56 56" fill="none" aria-hidden>
            <circle cx="28" cy="28" r="26" stroke="currentColor" strokeWidth="2" opacity="0.35" />
            <path d="M17 29.5 24.5 37 39 20" stroke="currentColor" strokeWidth="3" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
          <h2>Message sent.</h2>
          <p className="muted" style={{ marginInline: 'auto' }}>
            Thanks for writing — expect a reply within a couple of days.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="container form-page">
      <h1>Contact</h1>
      <p className="lede" style={{ marginBottom: 'var(--space-8)' }}>
        Questions, beta access, partnerships, or just opinions about Git clients — all welcome.
      </p>
      <form onSubmit={submit} noValidate>
        <div className="field">
          <label htmlFor="ct-name">Name</label>
          <input
            id="ct-name"
            type="text"
            autoComplete="name"
            placeholder="Ada Lovelace"
            value={name}
            onChange={(e) => setName(e.target.value)}
            required
          />
        </div>

        <div className="field">
          <label htmlFor="ct-email">Email</label>
          <input
            id="ct-email"
            type="email"
            autoComplete="email"
            placeholder="you@company.com"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            required
          />
        </div>

        <div className="field">
          <label htmlFor="ct-topic">Topic</label>
          <select id="ct-topic" value={topic} onChange={(e) => setTopic(e.target.value)}>
            {TOPICS.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>
        </div>

        <div className="field">
          <label htmlFor="ct-message">Message</label>
          <textarea
            id="ct-message"
            placeholder="What's on your mind?"
            value={message}
            maxLength={5000}
            onChange={(e) => setMessage(e.target.value)}
            required
          />
          <span className="muted" style={{ fontSize: '0.75rem', alignSelf: 'flex-end' }}>
            {message.length} / 5000
          </span>
        </div>

        <div className="hp-field" aria-hidden="true">
          <label htmlFor="ct-website">Leave this field empty</label>
          <input
            id="ct-website"
            type="text"
            tabIndex={-1}
            autoComplete="off"
            value={hp}
            onChange={(e) => setHp(e.target.value)}
          />
        </div>

        <Turnstile onToken={setToken} />

        {error && (
          <p className="form-alert" role="alert">
            {error}
          </p>
        )}

        <button type="submit" className="btn btn-accent btn-lg" disabled={busy}>
          {busy ? 'Sending…' : 'Send message'}
        </button>
      </form>
    </div>
  );
}
