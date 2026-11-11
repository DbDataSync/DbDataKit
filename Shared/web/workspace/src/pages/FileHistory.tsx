import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { DiffViewer, ErrorBanner } from '@dbdatakit/shell'
import { workspaceApi, type Commit } from '../api'
import { languageOf } from '../model'

const when = (iso: string) => (iso ? new Date(iso).toLocaleString() : '')

/** The earlier texts of a file, drawn without a query: the commits that changed it, each opening beside the current text, and the button that puts the chosen one into the editor (the file is saved as any edit is, then committed with the draft). */
export function FileHistoryView({ commits, loading, chosen, earlier, loadingText, current, path, canRestore, onChoose, onRestore }: { commits: readonly Commit[]; loading: boolean; chosen: string | null; earlier: string | null; loadingText: boolean; current: string; path: string; canRestore: boolean; onChoose: (sha: string) => void; onRestore: () => void }) {
  return (
    <div className="card" style={{ marginBottom: 10 }} data-testid="wsp-file-history">
      <div className="card-body">
        <div className="hint" style={{ marginBottom: 4 }}>The commits in which this file changed, newest first. A change not committed yet is the text in the editor.</div>
        {loading && <div className="empty">Loading...</div>}
        {!loading && commits.length === 0 && <div className="empty" data-testid="wsp-file-history-none">No commit has changed this file yet.</div>}
        {commits.map((c) => (
          <div key={c.sha} className={`grid-row ${chosen === c.sha ? 'active' : ''}`} style={{ gap: 12, flexWrap: 'wrap' }} data-testid="wsp-file-history-commit" data-sha={c.sha}>
            <button type="button" className="btn-link mono" data-testid="wsp-file-history-choose" aria-current={chosen === c.sha} onClick={() => onChoose(c.sha)}>{c.sha.slice(0, 8)}</button>
            <span>{c.message}</span>
            <span className="hint">{c.author}, {when(c.when)}</span>
          </div>
        ))}
        {chosen !== null && loadingText && <div className="empty">Loading the earlier text...</div>}
        {chosen !== null && earlier !== null && (
          <div style={{ marginTop: 8 }}>
            <div className="hint">{earlier === current ? 'The editor already has this text.' : 'On the left that commit\'s text, on the right the text in the editor.'}</div>
            <div data-testid="wsp-file-history-diff"><DiffViewer before={earlier} after={current} language={languageOf(path)} height={260} testId="wsp-file-history-diff-editor" /></div>
            <button type="button" className="btn btn-primary" style={{ marginTop: 6 }} disabled={!canRestore || earlier === current} title={canRestore ? undefined : 'Only the owner of the workspace, with the author role, edits it.'} data-testid="wsp-file-history-restore" onClick={onRestore}>Put this text into the editor</button>
          </div>
        )}
      </div>
    </div>
  )
}

/** The earlier texts of a file of a draft (plan 33 SC21e, the Store's History tab for every kind): read from the draft's own git history; restoring changes the editor's text only, so it can be undone by not saving and is a change like any other once committed. */
export function FileHistory({ project, id, path, current, canRestore, onRestore }: { project: string; id: string; path: string; current: string; canRestore: boolean; onRestore: (text: string) => void }) {
  const [chosen, setChosen] = useState<string | null>(null)
  const history = useQuery({ queryKey: ['workspace', 'file-history', project, id, path], queryFn: () => workspaceApi.fileHistory(project, id, path), gcTime: 0 })
  const text = useQuery({ queryKey: ['workspace', 'file-at', project, id, chosen, path], queryFn: () => workspaceApi.fileAt(project, id, chosen!, path), enabled: chosen !== null, staleTime: Infinity })
  return (
    <>
      <ErrorBanner error={history.error ?? text.error} />
      <FileHistoryView commits={history.data ?? []} loading={history.isLoading} chosen={chosen} earlier={chosen !== null ? text.data ?? null : null} loadingText={text.isLoading} current={current} path={path} canRestore={canRestore} onChoose={setChosen} onRestore={() => { if (text.data !== undefined) onRestore(text.data) }} />
    </>
  )
}
