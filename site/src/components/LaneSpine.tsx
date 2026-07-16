import { useEffect, useRef, useState } from 'react';
import { useTheme } from '../theme/ThemeProvider';

/**
 * The patrol line: five branch lanes run down the page's left gutter, braiding
 * as you scroll and posting a checkpoint beside each section marked with
 * `data-thread-node`. Checkpoints fill with their lane color as you pass them —
 * cleared. Lanes draw in with scroll progress; reduced-motion (and no-JS) gets
 * them fully drawn.
 *
 * Parent must be `position: relative` (the `.threaded` wrapper). The spine
 * extends past the wrapper to the top of the footer so the lanes run the
 * whole page. Hidden below 1360px via CSS — the gutter is too narrow there.
 */

const XS = [16, 30, 44, 58, 72]; // lane x positions inside the band
const SEG = 300; // braid segment height (px)

interface Braid {
  segs: number;
  perms: number[][];
  slotOf: (thread: number, s: number) => number;
}

function buildBraid(height: number): Braid {
  const segs = Math.max(Math.ceil(height / SEG), 1);
  // perms[s][slot] = which lane occupies band slot `slot` at boundary s.
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
  return { segs, perms, slotOf: (thread, s) => perms[s].indexOf(thread) };
}

function segmentBounds(braid: Braid, height: number, y: number) {
  const s = Math.min(Math.max(Math.floor(y / SEG), 0), braid.segs - 1);
  return { s, y0: s * SEG, y1: Math.min((s + 1) * SEG, height) };
}

/** Exact x of `thread` at height `y` — solves the segment's cubic for y. */
function threadXAt(braid: Braid, height: number, thread: number, y: number): number {
  const { s, y0, y1 } = segmentBounds(braid, height, y);
  const x0 = XS[braid.slotOf(thread, s)];
  const x1 = XS[braid.slotOf(thread, s + 1)];
  if (x0 === x1) return x0;
  const k = (y1 - y0) * 0.45;
  const yc = Math.min(Math.max(y, y0), y1);
  let lo = 0;
  let hi = 1;
  for (let i = 0; i < 24; i++) {
    const t = (lo + hi) / 2;
    const mt = 1 - t;
    const Y = mt * mt * mt * y0 + 3 * mt * mt * t * (y0 + k) + 3 * mt * t * t * (y1 - k) + t * t * t * y1;
    if (Y < yc) lo = t;
    else hi = t;
  }
  const t = (lo + hi) / 2;
  const mt = 1 - t;
  return mt * mt * mt * x0 + 3 * mt * mt * t * x0 + 3 * mt * t * t * x1 + t * t * t * x1;
}

function buildPaths(braid: Braid, height: number): string[] {
  return Array.from({ length: 5 }, (_, t) => {
    let d = `M ${XS[braid.slotOf(t, 0)]} 0`;
    for (let s = 1; s <= braid.segs; s++) {
      const x0 = XS[braid.slotOf(t, s - 1)];
      const x1 = XS[braid.slotOf(t, s)];
      const y0 = (s - 1) * SEG;
      const y1 = Math.min(s * SEG, height);
      const k = (y1 - y0) * 0.45;
      d += ` C ${x0} ${y0 + k}, ${x1} ${y1 - k}, ${x1} ${y1}`;
    }
    return d;
  });
}

interface Node {
  y: number;
  x: number;
  thread: number;
}

export function LaneSpine() {
  const ref = useRef<SVGSVGElement>(null);
  const { theme } = useTheme();
  const [height, setHeight] = useState(0);
  const [nodes, setNodes] = useState<Node[]>([]);
  const [progress, setProgress] = useState(1); // fully drawn until JS decides otherwise

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
      const braid = buildBraid(h);
      const found: Node[] = [];
      wrap.querySelectorAll<HTMLElement>('[data-thread-node]').forEach((el, i) => {
        const y = el.getBoundingClientRect().top + window.scrollY - wrapTop + 14;
        const thread = (i * 2 + 1) % 5;
        found.push({ y, x: threadXAt(braid, h, thread, y), thread });
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
    if (height === 0) return;
    const svg = ref.current;
    const wrap = svg?.parentElement;
    if (!wrap) return;
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

  const braid = buildBraid(height);
  const paths = buildPaths(braid, height);

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
            fill={passed ? `var(--lane-${n.thread + 1})` : 'var(--surface-window)'}
            stroke={`var(--lane-${n.thread + 1})`}
            strokeWidth="1.8"
            opacity={passed ? 1 : 0}
            style={{ transition: 'opacity 500ms var(--ease-out), fill 500ms var(--ease-out)' }}
          />
        );
      })}
    </svg>
  );
}
