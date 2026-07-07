import { useEffect, useRef } from 'react';
import { useTheme } from '../theme/ThemeProvider';

/**
 * The loom: commit-graph threads flowing left to right, switching lanes like
 * branches, then converging into a tight braid at the right edge — history
 * being woven. Canvas-drawn in the current theme's lane colors.
 *
 * Honors prefers-reduced-motion (renders one static frame), pauses when
 * offscreen or the tab is hidden.
 */

const THREAD_COUNT = 7;
const LANE_ROWS = 6;
const SEG_FREQ = 0.0021; // lane-change frequency along x
const DRIFT = 0.11; // history drift speed (segments/second)
const MAX_SEGS = 4000;

function hash(i: number, seg: number): number {
  const s = Math.sin(i * 127.1 + seg * 311.7) * 43758.5453;
  return s - Math.floor(s);
}

/** Per-thread lane schedule: random walk with steps of at most ±2 lanes, so
 *  transitions stay branch-like instead of cliff-like. */
function buildLanes(): number[][] {
  return Array.from({ length: THREAD_COUNT }, (_, i) => {
    const lanes = new Array<number>(MAX_SEGS);
    lanes[0] = Math.floor(hash(i, 0) * LANE_ROWS);
    for (let s = 1; s < MAX_SEGS; s++) {
      const step = Math.round((hash(i, s) - 0.5) * 4); // -2..2
      lanes[s] = Math.min(Math.max(lanes[s - 1] + step, 0), LANE_ROWS - 1);
    }
    return lanes;
  });
}

function smootherstep(t: number): number {
  return t * t * t * (t * (t * 6 - 15) + 10);
}

function easeOutQuint(t: number): number {
  return 1 - Math.pow(1 - t, 5);
}

export function WeaveHero() {
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const { theme } = useTheme();

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    const reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const styles = getComputedStyle(document.documentElement);
    const laneColors = [1, 2, 3, 4, 5].map((n) => styles.getPropertyValue(`--lane-${n}`).trim());
    const colors = Array.from({ length: THREAD_COUNT }, (_, i) => laneColors[i % laneColors.length]);
    const nodeFill = styles.getPropertyValue('--surface-window').trim();

    let raf = 0;
    let running = false;
    let width = 0;
    let height = 0;
    const started = performance.now();

    function resize() {
      if (!canvas) return;
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      width = canvas.clientWidth;
      height = canvas.clientHeight;
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
      ctx!.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    function laneY(lane: number): number {
      const pad = height * 0.16;
      return pad + (lane / (LANE_ROWS - 1)) * (height - pad * 2);
    }

    const laneSchedule = buildLanes();

    function threadY(i: number, x: number, t: number): number {
      const u = x * SEG_FREQ + t + i * 3.7;
      const seg = Math.min(Math.max(Math.floor(u), 0), MAX_SEGS - 2);
      const frac = u - seg;
      const from = laneSchedule[i][seg];
      const to = laneSchedule[i][seg + 1];
      // Run straight for 60% of a segment, then curve into the next lane.
      const blend = frac < 0.6 ? 0 : smootherstep((frac - 0.6) / 0.4);
      const y = laneY(from) + (laneY(to) - laneY(from)) * blend;
      // Converge into a tight interleaved band — the woven fabric — on the right.
      const xn = x / width;
      const w = smootherstep(Math.min(Math.max((xn - 0.7) / 0.26, 0), 1));
      const bandRow = (i - (THREAD_COUNT - 1) / 2) * 8;
      const over = Math.sin(x * 0.035 + i * 1.9 + t * 30) * 4;
      return y + (height / 2 + bandRow + over - y) * w;
    }

    function frame(now: number) {
      const t = (now - started) / 1000;
      const drift = t * DRIFT;
      const draw = reduced ? 1 : easeOutQuint(Math.min(t / 1.5, 1));
      ctx!.clearRect(0, 0, width, height);

      const xMax = width * draw;
      for (let i = 0; i < THREAD_COUNT; i++) {
        ctx!.beginPath();
        ctx!.strokeStyle = colors[i];
        ctx!.lineWidth = 1.8;
        ctx!.globalAlpha = 0.8;
        ctx!.lineJoin = 'round';
        for (let x = 0; x <= xMax; x += 6) {
          const y = threadY(i, x, drift);
          if (x === 0) ctx!.moveTo(x, y);
          else ctx!.lineTo(x, y);
        }
        ctx!.stroke();
      }

      // Commit nodes at lane changes, drifting left with history.
      ctx!.globalAlpha = 1;
      for (let i = 0; i < THREAD_COUNT; i++) {
        const uStart = i * 3.7 + drift;
        const segFirst = Math.max(Math.ceil(uStart), 1);
        const segLast = Math.min(Math.floor(xMax * SEG_FREQ + uStart), MAX_SEGS - 1);
        for (let seg = segFirst; seg <= segLast; seg++) {
          const from = laneSchedule[i][seg - 1];
          const to = laneSchedule[i][seg];
          if (from === to) continue;
          const x = (seg - uStart) / SEG_FREQ;
          if (x < 8 || x > width * 0.72) continue; // keep the braid clean
          const y = threadY(i, x + 1, drift);
          ctx!.beginPath();
          ctx!.arc(x, y, 3.2, 0, Math.PI * 2);
          ctx!.fillStyle = nodeFill;
          ctx!.strokeStyle = colors[i];
          ctx!.lineWidth = 1.8;
          ctx!.fill();
          ctx!.stroke();
        }
      }

      if (!reduced && running) raf = requestAnimationFrame(frame);
    }

    function start() {
      if (running) return;
      running = true;
      resize();
      raf = requestAnimationFrame(frame);
    }

    function stop() {
      running = false;
      cancelAnimationFrame(raf);
    }

    resize();
    if (reduced) {
      frame(started + 4000); // single, fully-drawn static frame
    } else {
      start();
    }

    const ro = new ResizeObserver(() => {
      resize();
      if (reduced) frame(started + 4000);
    });
    ro.observe(canvas);

    const io = new IntersectionObserver(
      ([entry]) => {
        if (reduced) return;
        if (entry.isIntersecting) start();
        else stop();
      },
      { threshold: 0.05 },
    );
    io.observe(canvas);

    const onVisibility = () => {
      if (reduced) return;
      if (document.hidden) stop();
      else start();
    };
    document.addEventListener('visibilitychange', onVisibility);

    return () => {
      stop();
      ro.disconnect();
      io.disconnect();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [theme]);

  return <canvas ref={canvasRef} className="weave-canvas" aria-hidden />;
}
