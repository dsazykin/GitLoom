import { useEffect, useRef } from 'react';
import { TURNSTILE_SITEKEY } from '../config';
import { useTheme } from '../theme/ThemeProvider';

declare global {
  interface Window {
    turnstile?: {
      render: (el: HTMLElement, opts: Record<string, unknown>) => string;
      remove: (id: string) => void;
    };
  }
}

let scriptPromise: Promise<void> | null = null;

function loadScript(): Promise<void> {
  if (window.turnstile) return Promise.resolve();
  scriptPromise ??= new Promise((resolve, reject) => {
    const s = document.createElement('script');
    s.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
    s.async = true;
    s.onload = () => resolve();
    s.onerror = () => {
      scriptPromise = null;
      reject(new Error('Failed to load Turnstile'));
    };
    document.head.appendChild(s);
  });
  return scriptPromise;
}

/** Cloudflare Turnstile widget; reports its token (or null on expiry) upward. */
export function Turnstile({ onToken }: { onToken: (token: string | null) => void }) {
  const holder = useRef<HTMLDivElement>(null);
  const { theme } = useTheme();
  const onTokenRef = useRef(onToken);
  onTokenRef.current = onToken;

  useEffect(() => {
    let widgetId: string | null = null;
    let cancelled = false;
    loadScript()
      .then(() => {
        if (cancelled || !holder.current || !window.turnstile) return;
        widgetId = window.turnstile.render(holder.current, {
          sitekey: TURNSTILE_SITEKEY,
          theme: theme === 'daylight' ? 'light' : 'dark',
          callback: (token: string) => onTokenRef.current(token),
          'expired-callback': () => onTokenRef.current(null),
          'error-callback': () => onTokenRef.current(null),
        });
      })
      .catch(() => onTokenRef.current(null));
    return () => {
      cancelled = true;
      if (widgetId && window.turnstile) window.turnstile.remove(widgetId);
    };
  }, [theme]);

  return <div ref={holder} style={{ minHeight: 65, marginBottom: 'var(--space-6)' }} />;
}
