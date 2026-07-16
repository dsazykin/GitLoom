import type { SVGProps } from 'react';

/**
 * Mainguard site icon set — 20px grid, 1.6px strokes, round joins.
 * Matches the app's icon language: precise, geometric, no fills.
 */

function base(props: SVGProps<SVGSVGElement>) {
  return {
    width: 20,
    height: 20,
    viewBox: '0 0 20 20',
    fill: 'none',
    stroke: 'currentColor',
    strokeWidth: 1.6,
    strokeLinecap: 'round' as const,
    strokeLinejoin: 'round' as const,
    'aria-hidden': true,
    ...props,
  };
}

export const IconGraph = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <circle cx="5" cy="4" r="1.8" />
    <circle cx="5" cy="16" r="1.8" />
    <circle cx="15" cy="10" r="1.8" />
    <path d="M5 5.8v8.4M6.5 5.2c4 1.5 8.5 1.5 8.5 4.8s-4.5 3.3-8.5 4.8" />
  </svg>
);

export const IconStage = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <rect x="3" y="3" width="14" height="14" rx="2.5" />
    <path d="M7 10h6M10 7v6" />
  </svg>
);

export const IconMerge = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <circle cx="5" cy="4" r="1.8" />
    <circle cx="5" cy="16" r="1.8" />
    <circle cx="15" cy="16" r="1.8" />
    <path d="M5 5.8v8.4M5 8c0 4 5.5 8 8.2 8" />
  </svg>
);

export const IconShield = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <path d="M10 2.5 4 5v5c0 4 2.8 6.5 6 7.5 3.2-1 6-3.5 6-7.5V5l-6-2.5Z" />
    <path d="m7.5 10 1.8 1.8L12.8 8" />
  </svg>
);

export const IconLock = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <rect x="4.5" y="9" width="11" height="8" rx="2" />
    <path d="M7 9V6.5a3 3 0 0 1 6 0V9" />
  </svg>
);

export const IconNoLock = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <rect x="4.5" y="9" width="11" height="8" rx="2" />
    <path d="M7 9V6.5a3 3 0 0 1 5.6-1.5" />
    <path d="m3 3 14 14" />
  </svg>
);

export const IconZap = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <path d="M11 2 4 11.5h5L9 18l7-9.5h-5L11 2Z" />
  </svg>
);

export const IconThreads = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <path d="M3 5c4.5 0 9.5 0 14 0M3 10c4.5 0 9.5 0 14 0M3 15c4.5 0 9.5 0 14 0" />
    <path d="M7 2.5v15M13 2.5v15" opacity="0.55" />
  </svg>
);

export const IconCloud = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <path d="M6 15.5a3.5 3.5 0 0 1-.4-7A5 5 0 0 1 15.3 9a3.3 3.3 0 0 1-.8 6.5H6Z" />
  </svg>
);

export const IconCheck = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <path d="m4 10.5 4 4L16 6" />
  </svg>
);

export const IconEye = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <path d="M2.5 10S5.5 4.5 10 4.5 17.5 10 17.5 10 14.5 15.5 10 15.5 2.5 10 2.5 10Z" />
    <circle cx="10" cy="10" r="2.2" />
  </svg>
);

export const IconKey = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <circle cx="7" cy="13" r="3.5" />
    <path d="m9.5 10.5 7-7M13 4l3 3M10.5 6.5l3 3" />
  </svg>
);

export const IconWorktree = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <rect x="3" y="3" width="6" height="6" rx="1.5" />
    <rect x="11" y="11" width="6" height="6" rx="1.5" />
    <rect x="11" y="3" width="6" height="6" rx="1.5" />
    <path d="M6 9v3.5A1.5 1.5 0 0 0 7.5 14H11" />
  </svg>
);

export const IconArrowRight = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <path d="M3.5 10h13M12 5.5l4.5 4.5-4.5 4.5" />
  </svg>
);

export const IconMail = (p: SVGProps<SVGSVGElement>) => (
  <svg {...base(p)}>
    <rect x="2.5" y="4.5" width="15" height="11" rx="2" />
    <path d="m3.5 6 6.5 5 6.5-5" />
  </svg>
);

export const IconGitHub = (p: SVGProps<SVGSVGElement>) => (
  <svg width={20} height={20} viewBox="0 0 20 20" fill="currentColor" aria-hidden {...p}>
    <path d="M10 1.6a8.4 8.4 0 0 0-2.66 16.38c.42.08.58-.18.58-.4v-1.55c-2.34.5-2.83-1-2.83-1-.38-.97-.93-1.23-.93-1.23-.77-.52.06-.51.06-.51.84.06 1.29.86 1.29.86.75 1.28 1.97.91 2.45.7.08-.55.3-.92.53-1.13-1.87-.21-3.83-.93-3.83-4.15 0-.92.33-1.67.86-2.26-.08-.21-.37-1.07.09-2.22 0 0 .7-.23 2.31.86a8 8 0 0 1 4.2 0c1.6-1.09 2.3-.86 2.3-.86.47 1.15.18 2.01.1 2.22.53.59.85 1.34.85 2.26 0 3.23-1.96 3.94-3.84 4.15.3.26.57.78.57 1.57v2.33c0 .22.15.49.58.4A8.4 8.4 0 0 0 10 1.6Z" />
  </svg>
);
