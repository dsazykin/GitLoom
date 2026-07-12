import { useEffect, useRef, type CSSProperties, type ReactNode } from 'react';

/**
 * Scroll-triggered reveal that only *enhances*: content renders visible, and is
 * hidden just-in-time on mount (JS-off, crawlers and reduced-motion users always
 * see it). `delay` staggers siblings.
 */
export function Reveal({
  children,
  delay = 0,
  className = '',
  style,
}: {
  children: ReactNode;
  delay?: number;
  className?: string;
  style?: CSSProperties;
}) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    // Already in view? Don't hide it at all — no pop-in on load.
    const rect = el.getBoundingClientRect();
    if (rect.top < window.innerHeight * 0.9) return;

    el.classList.add('reveal-pending');
    const io = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          // Also reveal when already scrolled past (anchor jumps, fast scrolls).
          if (entry.isIntersecting || entry.boundingClientRect.top < 0) {
            const reveal = () => {
              el.classList.add('reveal-in');
              el.classList.remove('reveal-pending');
            };
            if (delay > 0) setTimeout(reveal, delay);
            else reveal();
            io.disconnect();
          }
        }
      },
      { threshold: 0.15 },
    );
    io.observe(el);
    return () => io.disconnect();
  }, [delay]);

  return (
    <div ref={ref} className={className} style={style}>
      {children}
    </div>
  );
}
