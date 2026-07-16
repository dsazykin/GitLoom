/**
 * Deployment-specific constants for the Mainguard marketing site.
 *
 * The worker URL and GitHub URL still carry the legacy gitloom names: the
 * Cloudflare Worker keeps its deployed name until it is redeployed, and the
 * repo keeps its URL until the GitHub rename lands (see the rebrand plan in
 * docs/rebrand/). Update both here when those moves happen.
 */
export const API_BASE = 'https://gitloom-site-api.daniel-sazykin.workers.dev';
export const TURNSTILE_SITEKEY = '0x4AAAAAADw_X6swd7JbWQRB';
export const GITHUB_URL = 'https://github.com/dsazykin/GitLoom';
