/**
 * The GitLoom mark: three threads passing through a shuttle-shaped G,
 * drawn with the current theme's lane colors.
 */
export function Wordmark({ size = 26, withText = true }: { size?: number; withText?: boolean }) {
  return (
    <span
      style={{ display: 'inline-flex', alignItems: 'center', gap: '0.55rem', lineHeight: 1 }}
      aria-label="GitLoom"
    >
      <svg width={size} height={size} viewBox="0 0 26 26" fill="none" aria-hidden>
        <path
          d="M21 6.5A9.5 9.5 0 1 0 22.5 13v-1.5H14"
          stroke="var(--accent)"
          strokeWidth="2.6"
          strokeLinecap="round"
        />
        <path d="M1 9.5h11" stroke="var(--lane-2)" strokeWidth="1.7" strokeLinecap="round" />
        <path d="M4 17h13" stroke="var(--lane-3)" strokeWidth="1.7" strokeLinecap="round" />
      </svg>
      {withText && (
        <span
          style={{
            font: '650 1.18rem/1 var(--font-sans)',
            letterSpacing: '-0.02em',
            color: 'var(--text-primary)',
          }}
        >
          GitLoom
        </span>
      )}
    </span>
  );
}
