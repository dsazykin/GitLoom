import type { ReactNode } from 'react';

/** App-window chrome shared by every vignette. */
export function WindowFrame({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="window">
      <div className="window-bar">
        <span className="window-dot" />
        <span className="window-dot" />
        <span className="window-dot" />
        <span className="window-title">{title}</span>
      </div>
      <div className="window-body">{children}</div>
    </div>
  );
}
