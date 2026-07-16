import { useEffect, useRef } from 'react';
import { useTheme } from '../theme/ThemeProvider';

/**
 * The gate: agent branch lanes stream IN from the left (the pattern drifts
 * rightward — work arriving), straighten out as they approach, and pass the
 * verification gate — a prominent walled checkpoint at ~62% width — emerging
 * as one bright, guarded main line with verified changes pulsing along it.
 * Canvas-drawn in the current theme's colors.
 *
 * Honors prefers-reduced-motion (renders one static frame), pauses when
 * offscreen or the tab is hidden.
 */

const THREAD_COUNT = 7;
const LANE_ROWS = 6;
const SEG_FREQ = 0.0021; // lane-change frequency along x
const DRIFT = 0.14; // inbound drift speed (segments/second)
const MAX_SEGS = 4000;
const BASE_SEG = 3000; // start deep in the schedule so rightward drift has hours of runway
const GATE = 0.62; // gate x position as a fraction of width
const FUNNEL_START = 0.34; // lanes begin converging here…
const FUNNEL_END = 0.56; // …and run straight from here to the gate

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

export function GateHero() {
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
    const accent = styles.getPropertyValue('--accent').trim();
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

    // Subtracting the drift moves the lane pattern RIGHT over time — work arriving
    // at the gate, never history sliding away from it.
    function threadY(i: number, x: number, drift: number): number {
      const u = BASE_SEG + i * 3.7 + x * SEG_FREQ - drift;
      const seg = Math.min(Math.max(Math.floor(u), 0), MAX_SEGS - 2);
      const frac = u - seg;
      const from = laneSchedule[i][seg];
      const to = laneSchedule[i][seg + 1];
      // Run straight for 60% of a segment, then curve into the next lane.
      const blend = frac < 0.6 ? 0 : smootherstep((frac - 0.6) / 0.4);
      const y = laneY(from) + (laneY(to) - laneY(from)) * blend;
      // Straighten out: converge fully BEFORE the gate, then approach it dead level.
      const xn = x / width;
      const w = smootherstep(Math.min(Math.max((xn - FUNNEL_START) / (FUNNEL_END - FUNNEL_START), 0), 1));
      return y + (height / 2 - y) * w;
    }

    function frame(now: number) {
      const t = (now - started) / 1000;
      const drift = t * DRIFT;
      const draw = reduced ? 1 : easeOutQuint(Math.min(t / 1.5, 1));
      ctx!.clearRect(0, 0, width, height);

      const gx = width * GATE;
      const mid = height / 2;
      const pad = height * 0.16;
      const xMax = width * draw;

      // Agent lanes stream in and straighten toward the gate.
      for (let i = 0; i < THREAD_COUNT; i++) {
        ctx!.beginPath();
        ctx!.strokeStyle = colors[i];
        ctx!.lineWidth = 1.8;
        ctx!.globalAlpha = 0.8;
        ctx!.lineJoin = 'round';
        const stop = Math.min(xMax, gx);
        for (let x = 0; x <= stop; x += 6) {
          const y = threadY(i, x, drift);
          if (x === 0) ctx!.moveTo(x, y);
          else ctx!.lineTo(x, y);
        }
        ctx!.stroke();
      }

      // Commit nodes at lane changes, drifting right with the incoming work.
      ctx!.globalAlpha = 1;
      for (let i = 0; i < THREAD_COUNT; i++) {
        const uBase = BASE_SEG + i * 3.7 - drift;
        const segFirst = Math.max(Math.ceil(uBase), 1);
        const segLast = Math.min(Math.floor(uBase + xMax * SEG_FREQ), MAX_SEGS - 1);
        for (let seg = segFirst; seg <= segLast; seg++) {
          const from = laneSchedule[i][seg - 1];
          const to = laneSchedule[i][seg];
          if (from === to) continue;
          const x = (seg - uBase) / SEG_FREQ;
          if (x < 8 || x > width * (FUNNEL_START + 0.1)) continue; // keep the approach clean
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

      if (xMax >= gx) {
        // The gate wall: a faint full-height line the posts sit on.
        ctx!.strokeStyle = accent;
        ctx!.lineCap = 'round';
        ctx!.beginPath();
        ctx!.globalAlpha = 0.12;
        ctx!.lineWidth = 1;
        ctx!.moveTo(gx, pad * 0.5);
        ctx!.lineTo(gx, height - pad * 0.5);
        ctx!.stroke();

        // The gate posts — unmissable: heavy accent strokes with crenellated caps.
        ctx!.beginPath();
        ctx!.globalAlpha = 0.95;
        ctx!.lineWidth = 3.5;
        ctx!.moveTo(gx, pad);
        ctx!.lineTo(gx, mid - 16);
        ctx!.moveTo(gx, mid + 16);
        ctx!.lineTo(gx, height - pad);
        ctx!.stroke();
        ctx!.beginPath();
        ctx!.lineWidth = 3;
        for (const y of [pad, mid - 16, mid + 16, height - pad]) {
          ctx!.moveTo(gx - 6, y);
          ctx!.lineTo(gx + 6, y);
        }
        ctx!.stroke();

        // The verification eye — a slow, calm breath at the gap.
        const breathe = reduced ? 0.5 : 0.5 + 0.5 * Math.sin(t * 2.2);
        ctx!.beginPath();
        ctx!.globalAlpha = 0.35 + 0.35 * breathe;
        ctx!.lineWidth = 2;
        ctx!.arc(gx, mid, 7 + breathe * 2.5, 0, Math.PI * 2);
        ctx!.stroke();

        // Main, under guard: one bright line out of the gate.
        ctx!.beginPath();
        ctx!.globalAlpha = 0.18;
        ctx!.lineWidth = 7;
        ctx!.moveTo(gx + 12, mid);
        ctx!.lineTo(xMax, mid);
        ctx!.stroke();
        ctx!.beginPath();
        ctx!.globalAlpha = 1;
        ctx!.lineWidth = 2.6;
        ctx!.moveTo(gx + 12, mid);
        ctx!.lineTo(xMax, mid);
        ctx!.stroke();

        // Verified changes landing on main — each one flashes as it clears the gate.
        const span = width - gx;
        for (let k = 0; k < 3; k++) {
          const px = gx + 12 + ((reduced ? k * 0.33 * span : t * 80 + k * (span / 3)) % span);
          if (px > xMax - 4) continue;
          const nearGate = Math.max(0, 1 - (px - gx) / 60);
          const fade = 1 - ((px - gx) / span) * 0.5;
          ctx!.beginPath();
          ctx!.globalAlpha = fade;
          ctx!.fillStyle = accent;
          ctx!.arc(px, mid, 2.6 + nearGate * 1.6, 0, Math.PI * 2);
          ctx!.fill();
          if (nearGate > 0) {
            ctx!.beginPath();
            ctx!.globalAlpha = nearGate * 0.5;
            ctx!.lineWidth = 1.5;
            ctx!.arc(px, mid, 6 + (1 - nearGate) * 8, 0, Math.PI * 2);
            ctx!.stroke();
          }
        }
        ctx!.globalAlpha = 1;
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

  return <canvas ref={canvasRef} className="gate-canvas" aria-hidden />;
}
