import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { runnerApi, type ChannelHead, type PruneAnswer } from '../api'
import { pruneCandidates } from '../model'
import { Modal } from './Dialogs'

/**
 * Prune the versions of a package (plan 30 PB05). The dialog preselects every version but the ones a channel holds and the three newest, and shows the dry run first: the button that removes stays disabled until the dry run of exactly
 * this selection has been read.
 */
export function PruneDialog({ name, versions, heads, onCancel, onDone }: { name: string; versions: number[]; heads: ChannelHead[]; onCancel: () => void; onDone: (answer: unknown) => void }) {
  const [chosen, setChosen] = useState<number[]>(() => pruneCandidates(versions, heads))
  const [dry, setDry] = useState<{ answer: PruneAnswer; for: string } | null>(null)
  const key = chosen.join(',')
  const preview = useMutation({ mutationFn: () => runnerApi.prune(name, { versions: chosen, dry_run: true }), onSuccess: (answer) => setDry({ answer, for: key }) })
  const remove = useMutation({ mutationFn: () => runnerApi.prune(name, { versions: chosen, dry_run: false }), onSuccess: onDone })
  const ready = dry !== null && dry.for === key && chosen.length > 0
  const toggle = (v: number) => setChosen((c) => (c.includes(v) ? c.filter((x) => x !== v) : [...c, v].sort((a, b) => a - b)))
  return (
    <Modal label="Prune versions" testId="runner-prune-dialog" onCancel={onCancel}>
      <p>Choose the versions to remove. A version a channel serves, has staged or served before, and one another version depends on, is kept whatever is chosen.</p>
      <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
        {[...versions].sort((a, b) => a - b).map((v) => (
          <label key={v} className="row" style={{ gap: 4 }}><input type="checkbox" checked={chosen.includes(v)} onChange={() => toggle(v)} data-testid="runner-prune-version" data-version={v} />v{v}</label>
        ))}
      </div>
      {dry && dry.for === key && (
        <div data-testid="runner-prune-dry-run">
          <p>It would remove {dry.answer.remove.length === 0 ? 'nothing' : dry.answer.remove.map((v) => `v${v}`).join(', ')} and {dry.answer.files.length} file{dry.answer.files.length === 1 ? '' : 's'}.</p>
          {dry.answer.retained.length > 0 && <ul>{dry.answer.retained.map((r) => <li key={r.version}>v{r.version} stays: {r.reason}</li>)}</ul>}
        </div>
      )}
      <ErrorBanner error={preview.error ?? remove.error} />
      <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
        <button type="button" className="btn" onClick={onCancel}>Cancel</button>
        <button type="button" className="btn" disabled={chosen.length === 0 || preview.isPending} onClick={() => preview.mutate()} data-testid="runner-prune-preview">Show what it would remove</button>
        <button type="button" className="btn btn-danger" disabled={!ready || remove.isPending} onClick={() => remove.mutate()} data-testid="runner-prune-go">Prune</button>
      </div>
    </Modal>
  )
}
