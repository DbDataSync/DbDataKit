import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import { pruneCandidates } from '../models'
import type { PruneAnswer } from '../types'

/**
 * Prune the versions of a published definition (plan 30 PB05): the versions are preselected (all but the served one, the one before and the three newest), the dry run says what would go, and the button that removes stays
 * disabled until the dry run of exactly this selection has been read. An administrator's tool; nothing is shown to anyone else.
 */
export function PruneBox({ kind, name, versions, served, onDone }: { kind: string; name: string; versions: number[]; served: number | null; onDone: (answer: PruneAnswer) => void }) {
  const isAdmin = useIsAdmin()
  const [chosen, setChosen] = useState<number[]>(() => pruneCandidates(versions, served))
  const [dry, setDry] = useState<{ answer: PruneAnswer; for: string } | null>(null)
  const key = chosen.join(',')
  const preview = useMutation({ mutationFn: () => gatewayApi.definitions.prune(kind, name, { versions: chosen, dry_run: true }), onSuccess: (answer) => setDry({ answer, for: key }) })
  const remove = useMutation({ mutationFn: () => gatewayApi.definitions.prune(kind, name, { versions: chosen, dry_run: false }), onSuccess: (answer) => { setDry(null); onDone(answer) } })
  if (!isAdmin) return null
  const ready = dry !== null && dry.for === key && chosen.length > 0
  const toggle = (v: number) => setChosen((c) => (c.includes(v) ? c.filter((x) => x !== v) : [...c, v].sort((a, b) => a - b)))
  return (
    <div className="card" style={{ margin: '8px 0' }} data-testid="prune-box" data-kind={kind} data-name={name}>
      <strong>Prune versions of {name}</strong>
      <p className="hint">A version the group serves, has served before, or another version depends on is kept whatever is chosen.</p>
      <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
        {[...versions].sort((a, b) => a - b).map((v) => (
          <label key={v} className="row" style={{ gap: 4 }}><input type="checkbox" checked={chosen.includes(v)} onChange={() => toggle(v)} data-testid="prune-version" data-version={v} />v{v}</label>
        ))}
      </div>
      {dry && dry.for === key && (
        <div data-testid="prune-dry-run">
          <p>It would remove {dry.answer.remove.length === 0 ? 'nothing' : dry.answer.remove.map((v) => `v${v}`).join(', ')} and {dry.answer.files.length} file{dry.answer.files.length === 1 ? '' : 's'}.</p>
          {dry.answer.retained.length > 0 && <ul>{dry.answer.retained.map((r) => <li key={r.version}>v{r.version} stays: {r.reason}</li>)}</ul>}
        </div>
      )}
      <ErrorBanner error={preview.error ?? remove.error} />
      <div className="row" style={{ gap: 8 }}>
        <button type="button" className="btn btn-sm" disabled={chosen.length === 0 || preview.isPending} onClick={() => preview.mutate()} data-testid="prune-preview">Show what it would remove</button>
        <button type="button" className="btn btn-sm btn-danger" disabled={!ready || remove.isPending} onClick={() => remove.mutate()} data-testid="prune-go">Prune</button>
      </div>
    </div>
  )
}
