import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useIsAdmin } from '@dbdatakit/shell'
import type { DocumentVersions, ViewKind, ViewsClient } from '@dbdatakit/visuals'
import { lifecycleMessage, rollbackState, versionRows } from '../viewsVersions'
import { viewsClient } from '../viewsSource'
import { withSource } from '../viewsUrl'
import { PruneSection } from './PruneSection'

type Action = 'switch' | 'rollback' | 'retire'

export interface VersionsViewProps {
  kind: ViewKind
  name: string
  versions: DocumentVersions
  /** Whether the signed-in person may change what is served (the server checks again). */
  allowed: boolean
  busy: boolean
  /** The retire the person has been asked to confirm. */
  confirming: boolean
  result: { tone: 'ok' | 'error'; text: string } | null
  onSwitch: (version: number) => void
  onRollback: () => void
  onAskRetire: () => void
  onRetire: () => void
  onKeep: () => void
}

/** The versions of one published dashboard or report (plan 33, UE04): who published each, when and why; serve an earlier one; go back one; remove it from the group. Draws what it is given; `VersionsPanel` makes the calls. */
export function VersionsView(p: VersionsViewProps) {
  const rows = versionRows(p.versions, p.allowed)
  const back = rollbackState(p.versions, p.allowed)
  const retireBlocked = !p.allowed ? 'Removing a document from the group needs the author role.' : p.versions.active === null ? 'It is not served; there is nothing to remove.' : null
  return (
    <div className="card" style={{ margin: '8px 0' }} data-testid="views-versions-panel">
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <strong>Versions of {p.name}</strong>
        <span className="hint" data-testid="views-versions-served" data-version={p.versions.active ?? ''}>
          {p.versions.active === null ? 'Not served: it was removed from the group.' : `Version ${p.versions.active} is served to the group.`}
        </span>
      </div>
      {rows.length === 0
        ? <div className="empty" data-testid="views-versions-empty">No version of {p.name} is stored.</div>
        : (
          <table className="table" data-testid="views-versions-table">
            <thead><tr><th>Version</th><th>Published by</th><th>When</th><th>Note</th><th /></tr></thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.version} data-testid={`views-version-${r.version}`}>
                  <td>{r.version}{r.served && <span className="badge" data-testid={`views-version-served-${r.version}`} style={{ marginLeft: 6 }}>served</span>}</td>
                  <td>{r.who}</td>
                  <td>{r.when}</td>
                  <td>{r.note}</td>
                  <td>
                    <button type="button" className="btn btn-sm" data-testid={`views-version-switch-${r.version}`} disabled={p.busy || r.switchBlocked !== null} title={r.switchBlocked ?? `Serve version ${r.version} to the group`} onClick={() => p.onSwitch(r.version)}>Serve this version</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 8 }}>
        <button type="button" className="btn btn-sm" data-testid="views-version-rollback" disabled={p.busy || back.blocked !== null} title={back.blocked ?? `Go back to version ${back.version}`} onClick={p.onRollback}>
          {back.version === null ? 'Roll back' : `Roll back to version ${back.version}`}
        </button>
        {!p.confirming
          ? <button type="button" className="btn btn-sm" data-testid="views-version-retire" disabled={p.busy || retireBlocked !== null} title={retireBlocked ?? 'Stop serving it to the group; its versions are kept'} onClick={p.onAskRetire}>Remove from the group…</button>
          : (
            <>
              <span className="hint" data-testid="views-version-retire-ask">Stop serving {p.name} to the group? Its versions are kept and publishing it again brings it back.</span>
              <button type="button" className="btn btn-sm btn-primary" data-testid="views-version-retire-confirm" disabled={p.busy} onClick={p.onRetire}>Remove</button>
              <button type="button" className="btn btn-sm" data-testid="views-version-retire-cancel" onClick={p.onKeep}>Keep</button>
            </>
          )}
      </div>
      {p.result && <p role={p.result.tone === 'error' ? 'alert' : 'status'} data-testid={p.result.tone === 'error' ? 'views-versions-refused' : 'views-versions-result'}>{p.result.text}</p>}
    </div>
  )
}

/** The versions panel of a Gateway source's document: reads the history, and changes what is served through the Gateway, which checks the role and the version the person saw. */
export function VersionsPanel({ kind, name, source, keep, listPath }: { kind: ViewKind; name: string; source: string; keep: string | null; listPath: string }) {
  const allowed = useIsAdmin()
  const client = viewsClient(source) as Required<Pick<ViewsClient, 'versions' | 'switchTo' | 'rollback' | 'retire'>> & ViewsClient
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const key = ['views', 'versions', source, kind, name]
  const versions = useQuery({ queryKey: key, queryFn: () => client.versions(kind, name), retry: false })
  const [busy, setBusy] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const [result, setResult] = useState<VersionsViewProps['result']>(null)

  const run = async (action: Action, call: () => Promise<{ version: number | null; moved: string[] }>) => {
    setBusy(true)
    setResult(null)
    try {
      const done = await call()
      setResult({ tone: 'ok', text: lifecycleMessage(kind, name, action, done.version, done.moved) })
      setConfirming(false)
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: key }),
        queryClient.invalidateQueries({ queryKey: ['views', 'open', source, kind, name] }),
        queryClient.invalidateQueries({ queryKey: ['views', 'list'] }),
      ])
      if (action === 'retire') navigate(withSource(listPath, keep))
    } catch (e) {
      // the server's words, and what it serves now (a stale call is refused with the current version)
      setResult({ tone: 'error', text: e instanceof Error ? e.message : String(e) })
      setConfirming(false)
      await queryClient.invalidateQueries({ queryKey: key })
    } finally {
      setBusy(false)
    }
  }

  if (versions.isPending) return <div className="empty" data-testid="views-versions-loading" role="status">Reading the versions of {name}…</div>
  if (versions.isError) return <div className="empty" data-testid="views-versions-error" role="alert">The versions of {name} could not be read: {versions.error instanceof Error ? versions.error.message : String(versions.error)}</div>
  const seen = versions.data.active
  return (
    <>
    <VersionsView
      kind={kind} name={name} versions={versions.data} allowed={allowed} busy={busy} confirming={confirming} result={result}
      onSwitch={(v) => void run('switch', () => client.switchTo(kind, name, v, seen))}
      onRollback={() => void run('rollback', () => client.rollback(kind, name, seen))}
      onAskRetire={() => setConfirming(true)}
      onRetire={() => void run('retire', async () => { await client.retire(kind, name, seen); return { version: null, moved: [] } })}
      onKeep={() => setConfirming(false)}
    />
    {allowed && client.prune && (
      <PruneSection key={`${kind}:${name}:${versions.data.versions.length}`} kind={kind} name={name} versions={versions.data} client={client}
        onDone={(removed) => { setResult({ tone: 'ok', text: `Pruned ${name}: removed ${removed.length === 0 ? 'nothing' : removed.map((v) => `v${v}`).join(', ')}.` }); void queryClient.invalidateQueries({ queryKey: key }) }} />
    )}
    </>
  )
}
