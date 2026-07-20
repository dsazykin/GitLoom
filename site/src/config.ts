/**
 * Deployment-specific constants for the Mainguard marketing site.
 *
 * The GitHub URL still carries the legacy repo name until the GitHub rename
 * lands (rebrand plan Phase 2, docs/rebrand/) — update it here when it does.
 * The old mainguard-site-api worker stays deployed until traffic drains
 * (Phase 5), then gets deleted.
 */
export const API_BASE = 'https://mainguard-site-api.daniel-sazykin.workers.dev';
export const TURNSTILE_SITEKEY = '0x4AAAAAADw_X6swd7JbWQRB';
export const GITHUB_URL = 'https://github.com/dsazykin/Mainguard';
