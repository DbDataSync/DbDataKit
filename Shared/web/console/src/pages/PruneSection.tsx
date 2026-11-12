import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import type { DocumentVersions, PruneResult, ViewKind, ViewsClient } from '@dbdatakit/visuals'
import { pruneCandidates } from '../viewsVersions'

/**
 * Prune the versions of a published dashboard or report (plan 30 PB05, plan 33 UE04): the versions are preselected (all but the served, the previous and the three newest), the dry run says what would go, and the button that
 * removes stays disabled until the dry run of exactly this selection has been read. An administrator's tool: the Gateway refuses anyone else and the section is not drawn for them.
 */
export function PruneSection({ kind, name, versions, client, onDone }: { kind: ViewKind; name: string; versions: DocumentVersions; client: Pick<ViewsClient, 'prune'>; onDone: (removed: number[]) => void }) {
  const [chosen, setChosen] = useState<number[]>(() => pruneCandidates(versions))
  const [dry, setDry] = useState<{ answer: PruneResult; for: string } | null>(null)
  const key = chosen.join(',')
  const prune = client.prune
  const preview = useMutation({ mutationFn: () => prune!(kind, name, { versions: chosen, dryRun: true }), onSuccess: (answer) => setDry({ answer, for: key }) })
  const remove = useMutation({ mutationFn: () => prune!(kind, name, { versions: chosen, dryRun: false }), onSuccess: (answer) => { setDry(null); onDone(answer.remove) } })
  if (!prune) return null
  const ready = dry !== null && dry.for === key && chosen.length > 0
  const toggle = (v: number) => setChosen((c) => (c.includes(v) ? c.filter((x) => x !== v) : [...c, v].sort((a, b) => a - b)))
  const error = preview.error ?? remove.error
  return (
    <div className="card" style={{ margin: '8px 0' }} data-testid="views-prune-section">
      <strong>Prune versions of {name}</strong>
      <p className="hint">The served version, the one before it and a version another version depends on are kept whatever is chosen.</p>
      <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
        {[...versions.versions].sort((a, b) => a.version - b.version).map((v) => (
          <label key={v.version} className="row" style={{ gap: 4 }}><input type="checkbox" checked={chosen.includes(v.version)} onChange={() => toggle(v.version)} data-testid={`views-prune-version-${v.version}`} />v{v.version}</label>
        ))}
      </div>
      {dry && dry.for === key && (
        <div data-testid="views-prune-dry-run">
          <p>It would remove {dry.answer.remove.length === 0 ? 'nothing' : dry.answer.remove.map((v) => `v${v}`).join(', ')} and {dry.answer.files.length} file{dry.answer.files.length === 1 ? '' : 's'}.</p>
          {dry.answer.retained.length > 0 && <ul>{dry.answer.retained.map((r) => <li key={r.version}>v{r.version} stays: {r.reason}</li>)}</ul>}
        </div>
      )}
      {error && <p role="alert" data-testid="views-prune-error">{error instanceof Error ? error.message : String(error)}</p>}
      <div className="row" style={{ gap: 8 }}>
        <button type="button" className="btn btn-sm" disabled={chosen.length === 0 || preview.isPending} onClick={() => preview.mutate()} data-testid="views-prune-preview">Show what it would remove</button>
        <button type="button" className="btn btn-sm btn-danger" disabled={!ready || remove.isPending} onClick={() => remove.mutate()} data-testid="views-prune-go">Prune</button>
      </div>
    </div>
  )
}
