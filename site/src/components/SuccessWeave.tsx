import type { ReactNode } from 'react';

/**
 * Shared form-success animation: the five lane threads sweep across, weave a
 * ring, and the ring verifies with a drawn check — then the message rises in.
 * Pure CSS animation (`.sw-*` in site.css); reduced motion collapses it to the
 * finished frame via the global animation override.
 */
export function SuccessWeave({ title, children }: { title: string; children: ReactNode }) {
  const R = 30;
  const C = { x: 130, y: 62 };
  const arc = (i: number) => {
    // Five 64° arcs with 8° gaps, assembling the ring.
    const a0 = ((i * 72 - 90) * Math.PI) / 180;
    const a1 = ((i * 72 - 90 + 64) * Math.PI) / 180;
    const p0 = { x: C.x + R * Math.cos(a0), y: C.y + R * Math.sin(a0) };
    const p1 = { x: C.x + R * Math.cos(a1), y: C.y + R * Math.sin(a1) };
    return `M ${p0.x.toFixed(2)} ${p0.y.toFixed(2)} A ${R} ${R} 0 0 1 ${p1.x.toFixed(2)} ${p1.y.toFixed(2)}`;
  };

  return (
    <div className="success-weave" role="status">
      <svg width="260" height="124" viewBox="0 0 260 124" fill="none" aria-hidden>
        {/* Threads sweep in from the left and pass behind the ring. */}
        <g strokeWidth="1.6" strokeOpacity="0.55">
          {[0, 1, 2, 3, 4].map((i) => (
            <path
              key={i}
              className="sw-thread"
              style={{ animationDelay: `${i * 90}ms` }}
              stroke={`var(--lane-${i + 1})`}
              d={`M 0 ${22 + i * 20} C 60 ${22 + i * 20}, 78 62, ${C.x} 62 S 208 ${22 + i * 20}, 260 ${22 + i * 20}`}
              pathLength={1}
            />
          ))}
        </g>
        {/* The woven disc. */}
        <circle className="sw-disc" cx={C.x} cy={C.y} r={R + 8} fill="var(--surface-panel)" />
        {/* Ring assembles from five lane-colored arcs. */}
        <g strokeWidth="2.4" strokeLinecap="round">
          {[0, 1, 2, 3, 4].map((i) => (
            <path
              key={i}
              className="sw-arc"
              style={{ animationDelay: `${520 + i * 90}ms` }}
              stroke={`var(--lane-${i + 1})`}
              d={arc(i)}
              pathLength={1}
            />
          ))}
        </g>
        {/* The verification. */}
        <path
          className="sw-check"
          d={`M ${C.x - 12} ${C.y + 1} l 8 8 l 16 -18`}
          stroke="var(--success)"
          strokeWidth="3"
          strokeLinecap="round"
          strokeLinejoin="round"
          pathLength={1}
        />
      </svg>
      <div className="sw-copy">
        <h2>{title}</h2>
        {children}
      </div>
    </div>
  );
}
