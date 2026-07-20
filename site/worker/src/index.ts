/**
 * Mainguard site API — waitlist + contact form backend.
 * (Deployed worker name and URL still say mainguard until redeployed — see the rebrand plan.)
 *
 * Endpoints:
 *   POST /api/waitlist            { email, interests?, turnstileToken, website? }
 *   POST /api/contact             { name, email, topic?, message, turnstileToken, website? }
 *   GET  /api/admin/submissions   Authorization: Bearer <ADMIN_TOKEN>; ?kind=waitlist|contact&limit=N
 *
 * `website` is a honeypot: real users never fill it; bots that do get a fake success.
 * Every real submission is Turnstile-verified, rate-limited per IP, and stored in D1.
 * If RESEND_API_KEY is configured, a notification email is sent to NOTIFY_EMAIL.
 */

export interface Env {
  DB: D1Database;
  TURNSTILE_SECRET: string;
  ADMIN_TOKEN: string;
  NOTIFY_EMAIL: string;
  RESEND_API_KEY?: string;
}

const ALLOWED_ORIGINS = new Set([
  'https://mainguard.dev',
  'https://www.mainguard.dev',
  'https://dsazykin.github.io', // legacy Pages origin — drop after the domain cutover settles
  'http://localhost:5173',
  'http://localhost:4173',
]);

const RATE_LIMIT_PER_HOUR = 10;
// `weave` is Mainguard Cloud's original wire id; `cloud` is accepted so the
// site can migrate to it once this worker version is deployed.
const VALID_INTERESTS = new Set(['client', 'pro', 'weave', 'cloud']);
const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;

function corsHeaders(origin: string | null): Record<string, string> {
  const allowed = origin && ALLOWED_ORIGINS.has(origin) ? origin : 'https://mainguard.dev';
  return {
    'Access-Control-Allow-Origin': allowed,
    'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type, Authorization',
    'Access-Control-Max-Age': '86400',
    Vary: 'Origin',
  };
}

function json(body: unknown, status: number, origin: string | null): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...corsHeaders(origin) },
  });
}

async function sha256(input: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(input));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

async function verifyTurnstile(env: Env, token: string, ip: string): Promise<boolean> {
  if (!token) return false;
  const res = await fetch('https://challenges.cloudflare.com/turnstile/v0/siteverify', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ secret: env.TURNSTILE_SECRET, response: token, remoteip: ip }),
  });
  if (!res.ok) return false;
  const data = (await res.json()) as { success: boolean };
  return data.success === true;
}

async function isRateLimited(env: Env, ipHash: string): Promise<boolean> {
  const row = await env.DB.prepare(
    "SELECT COUNT(*) AS n FROM submissions WHERE ip_hash = ?1 AND created_at > strftime('%Y-%m-%dT%H:%M:%fZ', 'now', '-1 hour')",
  )
    .bind(ipHash)
    .first<{ n: number }>();
  return (row?.n ?? 0) >= RATE_LIMIT_PER_HOUR;
}

async function notify(env: Env, subject: string, text: string): Promise<void> {
  if (!env.RESEND_API_KEY) return;
  try {
    await fetch('https://api.resend.com/emails', {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${env.RESEND_API_KEY}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        from: 'Mainguard Site <onboarding@resend.dev>',
        to: [env.NOTIFY_EMAIL],
        subject,
        text,
      }),
    });
  } catch {
    // Notification failure must never fail the submission.
  }
}

type SubmissionBody = {
  email?: unknown;
  name?: unknown;
  topic?: unknown;
  message?: unknown;
  interests?: unknown;
  turnstileToken?: unknown;
  website?: unknown; // honeypot
};

function str(v: unknown, max: number): string | null {
  if (typeof v !== 'string') return null;
  const trimmed = v.trim();
  return trimmed.length > 0 && trimmed.length <= max ? trimmed : null;
}

async function guard(
  env: Env,
  request: Request,
  body: SubmissionBody,
  origin: string | null,
): Promise<{ ipHash: string } | Response> {
  // Honeypot: filled means bot — return a fake success and store nothing.
  if (typeof body.website === 'string' && body.website.trim() !== '') {
    return json({ ok: true }, 200, origin);
  }
  const ip = request.headers.get('CF-Connecting-IP') ?? '0.0.0.0';
  const ipHash = await sha256(`mainguard:${ip}`);
  if (await isRateLimited(env, ipHash)) {
    return json({ ok: false, error: 'Too many submissions. Please try again later.' }, 429, origin);
  }
  if (!(await verifyTurnstile(env, str(body.turnstileToken, 4096) ?? '', ip))) {
    return json(
      { ok: false, error: 'Anti-spam verification failed. Please refresh and try again.' },
      403,
      origin,
    );
  }
  return { ipHash };
}

async function handleWaitlist(request: Request, env: Env, origin: string | null): Promise<Response> {
  const body = (await request.json().catch(() => ({}))) as SubmissionBody;
  const email = str(body.email, 254)?.toLowerCase();
  if (!email || !EMAIL_RE.test(email)) {
    return json({ ok: false, error: 'Please enter a valid email address.' }, 400, origin);
  }
  const interests = Array.isArray(body.interests)
    ? body.interests.filter((i): i is string => typeof i === 'string' && VALID_INTERESTS.has(i))
    : [];

  const guarded = await guard(env, request, body, origin);
  if (guarded instanceof Response) return guarded;

  const ua = (request.headers.get('User-Agent') ?? '').slice(0, 256);
  await env.DB.prepare(
    `INSERT INTO submissions (kind, email, interests, ip_hash, user_agent)
     VALUES ('waitlist', ?1, ?2, ?3, ?4)
     ON CONFLICT (email) WHERE kind = 'waitlist'
     DO UPDATE SET interests = ?2`,
  )
    .bind(email, JSON.stringify(interests), guarded.ipHash, ua)
    .run();

  await notify(
    env,
    'Mainguard waitlist signup',
    `${email}\nInterests: ${interests.join(', ') || '(none selected)'}`,
  );
  return json({ ok: true }, 200, origin);
}

async function handleContact(request: Request, env: Env, origin: string | null): Promise<Response> {
  const body = (await request.json().catch(() => ({}))) as SubmissionBody;
  const email = str(body.email, 254)?.toLowerCase();
  const name = str(body.name, 100);
  const topic = str(body.topic, 100);
  const message = str(body.message, 5000);
  if (!email || !EMAIL_RE.test(email)) {
    return json({ ok: false, error: 'Please enter a valid email address.' }, 400, origin);
  }
  if (!name) return json({ ok: false, error: 'Please enter your name.' }, 400, origin);
  if (!message) {
    return json({ ok: false, error: 'Please enter a message (max 5000 characters).' }, 400, origin);
  }

  const guarded = await guard(env, request, body, origin);
  if (guarded instanceof Response) return guarded;

  const ua = (request.headers.get('User-Agent') ?? '').slice(0, 256);
  await env.DB.prepare(
    `INSERT INTO submissions (kind, email, name, topic, message, ip_hash, user_agent)
     VALUES ('contact', ?1, ?2, ?3, ?4, ?5, ?6)`,
  )
    .bind(email, name, topic, message, guarded.ipHash, ua)
    .run();

  await notify(
    env,
    `Mainguard contact: ${topic ?? 'General'}`,
    `From: ${name} <${email}>\nTopic: ${topic ?? '(none)'}\n\n${message}`,
  );
  return json({ ok: true }, 200, origin);
}

async function handleAdmin(request: Request, env: Env, origin: string | null): Promise<Response> {
  const auth = request.headers.get('Authorization') ?? '';
  if (auth !== `Bearer ${env.ADMIN_TOKEN}`) {
    return json({ ok: false, error: 'Unauthorized.' }, 401, origin);
  }
  const url = new URL(request.url);
  const kind = url.searchParams.get('kind');
  const limit = Math.min(Number(url.searchParams.get('limit')) || 100, 1000);
  const query =
    kind === 'waitlist' || kind === 'contact'
      ? env.DB.prepare(
          'SELECT id, kind, email, name, topic, message, interests, created_at FROM submissions WHERE kind = ?1 ORDER BY id DESC LIMIT ?2',
        ).bind(kind, limit)
      : env.DB.prepare(
          'SELECT id, kind, email, name, topic, message, interests, created_at FROM submissions ORDER BY id DESC LIMIT ?1',
        ).bind(limit);
  const { results } = await query.all();
  return json({ ok: true, count: results.length, submissions: results }, 200, origin);
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const origin = request.headers.get('Origin');
    const { pathname } = new URL(request.url);

    if (request.method === 'OPTIONS') {
      return new Response(null, { status: 204, headers: corsHeaders(origin) });
    }

    try {
      if (request.method === 'POST' && pathname === '/api/waitlist') {
        return await handleWaitlist(request, env, origin);
      }
      if (request.method === 'POST' && pathname === '/api/contact') {
        return await handleContact(request, env, origin);
      }
      if (request.method === 'GET' && pathname === '/api/admin/submissions') {
        return await handleAdmin(request, env, origin);
      }
      return json({ ok: false, error: 'Not found.' }, 404, origin);
    } catch (err) {
      console.error('Unhandled error', err);
      return json({ ok: false, error: 'Something went wrong. Please try again.' }, 500, origin);
    }
  },
} satisfies ExportedHandler<Env>;
