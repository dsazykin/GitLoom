import { useEffect, useRef, useState } from 'react';
import { useTheme } from '../theme/ThemeProvider';

/**
 * The loom's warp, continued: after the hero, five lane threads run down the
 * page's left selvage, braiding as you scroll and dropping a commit node beside
 * each section marked with `data-thread-node`. Threads draw in with scroll
 * progress; reduced-motion (and no-JS) gets them fully drawn.
 *
 * Parent must be `position: relative` (the `.threaded` wrapper). Hidden below
 * 1360px via CSS — the gutter is too narrow there.
 */

const XS = [16, 30, 44, 58, 72]; // thread x positions inside the band
const SEG = 300; // braid segment height (px)

interface Node {
  y: number;
  x: number;
  lane: number;
}

function buildPaths(height: number): { paths: string[]; laneAt: (y: number) => number[] } {
  const segs = Math.max(Math.ceil(height / SEG), 1);
  // perm[s][lane] = which thread occupies band slot `lane` at segment boundary s.
  const perms: number[][] = [[0, 1, 2, 3, 4]];
  for (let s = 1; s <= segs; s++) {
    const prev = perms[s - 1].slice();
    if (s % 2 === 1) {
      [prev[0], prev[1]] = [prev[1], prev[0]];
      [prev[2], prev[3]] = [prev[3], prev[2]];
    } else {
      [prev[1], prev[2]] = [prev[2], prev[1]];
      [prev[3], prev[4]] = [prev[4], prev[3]];
    }
    perms.push(prev);
  }
  const slotOf = (thread: number, s: number) => perms[s].indexOf(thread);
  const paths = Array.from({ length: 5 }, (_, t) => {
    let d = `M ${XS[slotOf(t, 0)]} 0`;
    for (let s = 1; s <= segs; s++) {
      const x0 = XS[slotOf(t, s - 1)];
      const x1 = XS[slotOf(t, s)];
      const y0 = (s - 1) * SEG;
      const y1 = Math.min(s * SEG, height);
      const k = (y1 - y0) * 0.45;
      d += ` C ${x0} ${y0 + k}, ${x1} ${y1 - k}, ${x1} ${y1}`;
    }
    return d;
  });
  const laneAt = (y: number) => {
    const s = Math.min(Math.floor(y / SEG), segs);
    return perms[s];
  };
  return { paths, laneAt };
}

export function ThreadSpine() {
  const ref = useRef<SVGSVGElement>(null);
  const { theme } = useTheme();
  const [height, setHeight] = useState(0);
  const [nodes, setNodes] = useState<Node[]>([]);
  const [progress, setProgress] = useState(1); // fully drawn until JS decides otherwise

  // Measure the wrapper and locate section anchors.
  useEffect(() => {
    const svg = ref.current;
    const wrap = svg?.parentElement;
    if (!svg || !wrap) return;

    const measure = () => {
      const h = wrap.scrollHeight;
      setHeight(h);
      const { laneAt } = buildPaths(h);
      const wrapTop = wrap.getBoundingClientRect().top + window.scrollY;
      const found: Node[] = [];
      wrap.querySelectorAll<HTMLElement>('[data-thread-node]').forEach((el, i) => {
        const y = el.getBoundingClientRect().top + window.scrollY - wrapTop + 14;
        const perm = laneAt(y);
        const slot = i % 5;
        found.push({ y, x: XS[slot], lane: perm[slot] });
      });
      setNodes(found);
    };

    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(wrap);
    return () => ro.disconnect();
  }, [theme]);

  // Scroll-linked draw progress.
  useEffect(() => {
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
    const svg = ref.current;
    const wrap = svg?.parentElement;
    if (!wrap) return;
    let raf = 0;
    const update = () => {
      raf = 0;
      const rect = wrap.getBoundingClientRect();
      const p = (window.innerHeight * 0.85 - rect.top) / rect.height;
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

  const { paths } = buildPaths(height);

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
      <g fill="none" strokeWidth="1.6" strokeOpacity="0.55">
        {paths.map((d, i) => (
          <path
            key={i}
            d={d}
            stroke={`var(--lane-${i + 1})`}
            pathLength={1}
            strokeDasharray={1}
            strokeDashoffset={Math.max(1 - progress * (1 + i * 0.05), 0)}
          />
        ))}
      </g>
      {nodes.map((n, i) => {
        const passed = progress * height >= n.y - 40;
        return (
          <circle
            key={i}
            cx={n.x}
            cy={n.y}
            r="4.5"
            fill="var(--surface-window)"
            stroke={`var(--lane-${n.lane + 1})`}
            strokeWidth="1.8"
            opacity={passed ? 1 : 0}
            style={{ transition: 'opacity 500ms var(--ease-out)' }}
          />
        );
      })}
    </svg>
  );
}
