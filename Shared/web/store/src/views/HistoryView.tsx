import type { ReactNode } from 'react'
import type { HistoryEntry } from '../api'
import { authorText, kindText } from '../model'
import { Gated } from './ModelEditorView'

export interface HistoryViewProps {
  versions: HistoryEntry[]
  /** The version whose text is shown against the current one. */
  selected: string | null
  onSelect: (version: string) => void
  /** The diff of the chosen text against the model's current text (a viewer is drawn by the page). */
  diff: ReactNode
  restore: { allowed: boolean; reason: string | null }
  onRestore: () => void
  /** What the last restore did, in a sentence. */
  done: string | null
  refusal: string | null
  /** The model's file is gone: its history is still here to read. */
  removed: boolean
}

/** The model's earlier texts, newest first, with who and when; the chosen one is compared with the text the model holds now, and Restore saves it again as a new version. */
export function HistoryView(p: HistoryViewProps) {
  return (
    <div data-testid="store-history">
      <p className="hint">Every text saved is kept. Restoring one saves it again as the newest version (checked like any save); nothing is removed from this list.</p>
      {p.removed && <div className="banner error" role="status" data-testid="history-removed"><span className="mark">!</span><span>The model was removed from this server; its earlier texts can still be read.</span></div>}
      {p.versions.length === 0 && <div className="empty" data-testid="history-empty">No earlier text is kept for this model.</div>}
      <div className="card flush" style={{ marginBottom: 12 }}>
        {p.versions.map((v, i) => (
          <button
            key={`${v.version}-${v.at}-${i}`} type="button" className="row" data-testid="history-entry" data-version={v.version} data-selected={v.version === p.selected} data-current={v.current} data-kind={v.kind}
            style={{ gap: 12, padding: '6px 14px', alignItems: 'baseline', width: '100%', textAlign: 'left', background: v.version === p.selected ? 'rgba(127,127,127,.12)' : 'transparent', border: 0, cursor: 'pointer', color: 'inherit' }}
            onClick={() => p.onSelect(v.version)}
          >
            <span className="mono" style={{ whiteSpace: 'nowrap' }}>{v.version.slice(0, 8)}</span>
            <span>{kindText(v.kind)} by {authorText(v)}</span>
            <span className="hint">{new Date(v.at).toLocaleString()}</span>
            {v.current && <span className="hint" data-testid="history-current">current</span>}
          </button>
        ))}
      </div>
      {p.selected && (
        <div data-testid="history-compare">
          <div className="row" style={{ gap: 12, alignItems: 'center', marginBottom: 8 }}>
            <span className="hint">Version <span className="mono">{p.selected.slice(0, 8)}</span> against the model now</span>
            <Gated testId="history-restore" label="Restore this version" className="btn btn-primary" allowed={p.restore.allowed} reason={p.restore.reason} onClick={p.onRestore} />
          </div>
          {p.diff}
        </div>
      )}
      {p.done && <div className="banner" role="status" data-testid="history-done">{p.done}</div>}
      {p.refusal && <div className="banner error" role="alert" data-testid="history-refusal"><span className="mark">!</span><span>{p.refusal}</span></div>}
    </div>
  )
}
