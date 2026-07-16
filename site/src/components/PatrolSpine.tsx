import { useEffect, useRef, useState } from 'react';
import { useTheme } from '../theme/ThemeProvider';

/**
 * The patrol: a single quiet accent line walks down the page's left gutter as
 * you scroll — the head of the drawn line is the sentry's light — posting a
 * checkpoint beside each section marked with `data-thread-node`. Checkpoints
 * clear (fill with Success) once the patrol passes them. Reduced-motion (and
 * no-JS) gets the route fully walked and every checkpoint cleared.
 *
 * Parent must be `position: relative` (the `.threaded` wrapper). The spine
 * extends past the wrapper to the top of the footer so the route runs the
 * whole page. Hidden below 1360px via CSS — the gutter is too narrow there.
 */

const X = 44; // the patrol route's x inside the 88px band

interface Node {
  y: number;
}

export function PatrolSpine() {
  const ref = useRef<SVGSVGElement>(null);
  const { theme } = useTheme();
  const [height, setHeight] = useState(0);
  const [nodes, setNodes] = useState<Node[]>([]);
  const [progress, setProgress] = useState(1); // fully walked until JS decides otherwise
  const [animated, setAnimated] = useState(false);

  // Measure the wrapper (extended to the footer) and locate section anchors.
  useEffect(() => {
    const svg = ref.current;
    const wrap = svg?.parentElement;
    if (!svg || !wrap) return;

    const measure = () => {
      const wrapTop = wrap.getBoundingClientRect().top + window.scrollY;
      let h = wrap.scrollHeight;
      const footer = document.querySelector('.footer');
      if (footer) {
        const footerTop = footer.getBoundingClientRect().top + window.scrollY;
        h = Math.max(h, footerTop - wrapTop);
      }
      setHeight(h);
      const found: Node[] = [];
      wrap.querySelectorAll<HTMLElement>('[data-thread-node]').forEach((el) => {
        found.push({ y: el.getBoundingClientRect().top + window.scrollY - wrapTop + 14 });
      });
      setNodes(found);
    };

    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(wrap);
    return () => ro.disconnect();
  }, [theme]);

  // Scroll-linked walk progress.
  useEffect(() => {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    if (height === 0) return;
    const svg = ref.current;
    const wrap = svg?.parentElement;
    if (!wrap) return;
    setAnimated(true);
    let raf = 0;
    const update = () => {
      raf = 0;
      const rect = wrap.getBoundingClientRect();
      const p = (window.innerHeight * 0.85 - rect.top) / height;
      setProgress(Math.min(Math.max(p, 0), 1));
    };
    const onScroll = () => {
      if (!raf) raf = requestAnimationFrame(update);
    };
    update();
    window.addEventListener('scroll', onScroll, { passive: true });
    window.addEventListener('resize', onScroll);
    return () => {
      window.removeEventListener('scroll', onScroll);
      window.removeEventListener('resize', onScroll);
      if (raf) cancelAnimationFrame(raf);
    };
  }, [height]);

  if (height === 0) {
    return <svg ref={ref} className="thread-spine" aria-hidden />;
  }

  const headY = progress * height;

  return (
    <svg
      ref={ref}
      className="thread-spine"
      aria-hidden
      width="88"
      height={height}
      viewBox={`0 0 88 ${height}`}
      preserveAspectRatio="none"
    >
      {/* The route, faint — where the patrol has yet to walk. */}
      <line x1={X} y1={0} x2={X} y2={height} stroke="var(--accent)" strokeOpacity="0.12" strokeWidth="1.6" />
      {/* The walked portion. */}
      <line x1={X} y1={0} x2={X} y2={headY} stroke="var(--accent)" strokeOpacity="0.55" strokeWidth="1.6" />
      {/* The sentry's light at the head of the walk. */}
      {animated && progress > 0.001 && progress < 0.999 && (
        <g>
          <circle cx={X} cy={headY} r="8" fill="none" stroke="var(--accent)" strokeOpacity="0.25" strokeWidth="1.5" />
          <circle cx={X} cy={headY} r="3.2" fill="var(--accent)" />
        </g>
      )}
      {/* Checkpoints: cleared once the patrol has passed. */}
      {nodes.map((n, i) => {
        const cleared = headY >= n.y - 40;
        return (
          <g key={i} style={{ transition: 'opacity 400ms var(--ease-out)' }} opacity={cleared ? 1 : 0.7}>
            <circle
              cx={X}
              cy={n.y}
              r="5"
              fill={cleared ? 'var(--success)' : 'var(--surface-window)'}
              stroke={cleared ? 'var(--success)' : 'var(--border-hairline)'}
              strokeWidth="1.8"
              style={{ transition: 'fill 400ms var(--ease-out), stroke 400ms var(--ease-out)' }}
            />
            {cleared && (
              <path
                d={`M ${X - 2.4} ${n.y + 0.2} l 1.8 1.8 l 3.2 -3.6`}
                stroke="var(--on-accent)"
                strokeWidth="1.5"
                strokeLinecap="round"
                strokeLinejoin="round"
                fill="none"
              />
            )}
          </g>
        );
      })}
    </svg>
  );
}
