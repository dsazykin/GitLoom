import { useState } from 'react';
import { WindowFrame } from './WindowFrame';

const LEFT = ['Parser.cs', 'Tokenizer.cs', 'AstBuilder.cs'];
const RIGHT = ['Parser.cs', 'DbContext.cs', 'Migrations/'];

/** Cross-worktree conflict radar — the shared file is flagged before it burns anyone. */
export function RadarVignette() {
  const [rebased, setRebased] = useState(false);

  return (
    <WindowFrame title="gitloom pro — conflict radar">
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }}>
        {[
          { title: 'claude-code · wt-1', files: LEFT },
          { title: 'opencode · wt-2', files: RIGHT },
        ].map((col) => (
          <div key={col.title} style={{ display: 'grid', gap: 6, alignContent: 'start' }}>
            <span className="mono" style={{ fontSize: 10.5, color: 'var(--text-muted)' }}>{col.title}</span>
            {col.files.map((f) => {
              const hot = f === 'Parser.cs' && !rebased;
              return (
                <span
                  key={f}
                  className={`mono ${hot ? 'vg-radar-link' : ''}`}
                  style={{
                    fontSize: 11.5,
                    padding: '6px 9px',
                    borderRadius: 'var(--radius-sm)',
                    border: `1px solid ${hot ? 'var(--warning)' : 'var(--border-hairline)'}`,
                    background: hot ? 'transparent' : 'var(--surface-card)',
                    color: hot ? 'var(--warning)' : 'var(--text-primary)',
                    transition: 'border-color 300ms var(--ease-out), color 300ms var(--ease-out)',
                  }}
                >
                  {hot ? '⚠ ' : ''}
                  {f}
                </span>
              );
            })}
          </div>
        ))}
      </div>
      <div
        aria-live="polite"
        style={{
          marginTop: 10,
          padding: '9px 11px',
          borderRadius: 'var(--radius-sm)',
          border: `1px solid ${rebased ? 'var(--success)' : 'var(--warning)'}`,
          transition: 'border-color 300ms var(--ease-out)',
        }}
      >
        <span className="mono" style={{ fontSize: 11.5, color: rebased ? 'var(--success)' : 'var(--warning)' }}>
          {rebased
            ? '✓ wt-2 rebased onto wt-1 — divergence resolved before it became a conflict'
            : '⚠ both agents editing Parser.cs — divergence predicted at symbol Parse()'}
        </span>
      </div>
      {!rebased ? (
        <button type="button" className="vg-replay" style={{ marginTop: 10 }} onClick={() => setRebased(true)}>
          resolve: rebase wt-2 now
        </button>
      ) : (
        <button type="button" className="vg-replay" style={{ marginTop: 10 }} onClick={() => setRebased(false)}>
          ↻ replay detection
        </button>
      )}
    </WindowFrame>
  );
}
