-- GitLoom site submissions. Applied with:
--   npx wrangler d1 execute gitloom-site --remote --file=schema.sql
CREATE TABLE IF NOT EXISTS submissions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  kind TEXT NOT NULL CHECK (kind IN ('waitlist', 'contact')),
  email TEXT NOT NULL,
  name TEXT,
  topic TEXT,
  message TEXT,
  interests TEXT,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
  ip_hash TEXT,
  user_agent TEXT
);

-- One waitlist row per email; repeat signups update interests instead.
CREATE UNIQUE INDEX IF NOT EXISTS idx_waitlist_email
  ON submissions (email) WHERE kind = 'waitlist';

-- Rate-limit lookups.
CREATE INDEX IF NOT EXISTS idx_ip_time ON submissions (ip_hash, created_at);
