import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { runnerApi, type RunnerInstance } from '../api'
import { adminLabels, labelProblem, labelsBlock, labelsRequest, normalizeLabel, reportedLabels, withLabel, withoutLabel } from '../labels'

export interface InstanceLabelsViewProps {
  instance: RunnerInstance
  /** Whether the person is offered the editor (the admin role; the runner checks again). */
  canEdit: boolean
  /** The administrator's labels as they are being edited. */
  draft: string[]
  /** What was typed in the add box. */
  typed: string
  busy: boolean
  /** What the last save added and removed, shown until the next change. */
  saved: { added: string[]; removed: string[] } | null
  /** The last call that was refused or failed. */
  failure: unknown
  onTyped: (text: string) => void
  onAdd: () => void
  onRemove: (label: string) => void
  onSave: () => void
  onCancel: () => void
}

/** The labels of one instance without their calls: the machine's own (read-only), the administrator's with a remove each, the box to add one, and Save for the whole list. Hook-free so a test can draw it and press its buttons. */
export function InstanceLabelsView(p: InstanceLabelsViewProps) {
  const reported = reportedLabels(p.instance)
  const block = labelsBlock(p.canEdit, p.instance, p.draft, p.typed, reported)
  const addProblem = labelProblem(p.typed, p.draft, reported)
  // a runner that is gone has no labels to change: the editor shows them and says so
  const editable = p.canEdit && p.instance.live !== false
  return (
    <div style={{ gridColumn: '1 / -1' }} className="card" data-testid="runner-labels-editor" data-instance={p.instance.id}>
      <div className="card-body">
        <div className="hint">Labels route work: a task that requires a label is claimed only by a runner that has it. The machine reports some labels about itself (marked reported); those cannot be changed. The others are an administrator&apos;s.</div>
        <ErrorBanner error={p.failure} />
        <div className="row" style={{ gap: 6, flexWrap: 'wrap', margin: '8px 0' }} data-testid="runner-labels-draft">
          {reported.map((l) => <span key={`r-${l}`} className="chip" data-source="reported" data-testid="runner-label-reported" title="reported by the machine">{l}</span>)}
          {p.draft.map((l) => (
            <span key={l} className="chip" data-source="admin" data-testid="runner-label-admin" data-label={l}>
              {l}
              {editable && <button type="button" className="btn btn-sm" aria-label={`Remove ${l}`} onClick={() => p.onRemove(l)} data-testid="runner-label-remove" style={{ marginLeft: 6 }}>x</button>}
            </span>
          ))}
          {p.draft.length === 0 && <span className="hint" data-testid="runner-labels-none">No label set by an administrator.</span>}
        </div>
        {editable && (
          <div className="row" style={{ gap: 8 }}>
            <input className="input mono" value={p.typed} placeholder="for example gpu or site:oslo" aria-label="Label to add" onChange={(e) => p.onTyped(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); p.onAdd() } }} data-testid="runner-label-input" style={{ width: 240 }} />
            <button type="button" className="btn" disabled={normalizeLabel(p.typed) === '' || addProblem !== null} onClick={p.onAdd} data-testid="runner-label-add">Add</button>
          </div>
        )}
        {addProblem && <div className="field-error" role="alert" data-testid="runner-label-problem">{addProblem}</div>}
        {p.saved && <div className="hint" data-testid="runner-labels-saved">{[p.saved.added.length > 0 ? `Added ${p.saved.added.join(', ')}.` : '', p.saved.removed.length > 0 ? `Removed ${p.saved.removed.join(', ')}.` : '', p.saved.added.length + p.saved.removed.length === 0 ? 'Nothing changed.' : ''].filter(Boolean).join(' ')} A task that waited for one starts within a few seconds.</div>}
        <div className="row" style={{ gap: 12, marginTop: 8 }}>
          <button type="button" className="btn btn-primary" disabled={block !== null || p.busy} onClick={p.onSave} data-testid="runner-labels-save">{p.busy ? 'Saving...' : 'Save labels'}</button>
          <button type="button" className="btn" onClick={p.onCancel} data-testid="runner-labels-close">Close</button>
          {block !== null && !p.busy && <span className="hint" data-testid="runner-labels-why">{block}</span>}
        </div>
      </div>
    </div>
  )
}

/** The labels of an instance of the runner group (plan 33, UE11): edited as a list and saved as a whole through `PUT instances/{id}/labels`; the runner decides the role and audits each change. */
export function InstanceLabels({ instance, canEdit, onClose }: { instance: RunnerInstance; canEdit: boolean; onClose: () => void }) {
  const client = useQueryClient()
  const [draft, setDraft] = useState<string[]>(() => adminLabels(instance))
  const [typed, setTyped] = useState('')
  const [saved, setSaved] = useState<{ added: string[]; removed: string[] } | null>(null)
  const reported = reportedLabels(instance)
  const save = useMutation({
    mutationFn: () => runnerApi.setLabels(instance.id, labelsRequest(draft).labels),
    onSuccess: (answer) => {
      setSaved({ added: answer.added, removed: answer.removed })
      setDraft(answer.labels.filter((l) => l.source === 'admin').map((l) => l.label))
      void client.invalidateQueries({ queryKey: ['runner'] })
    },
  })
  const change = (next: string[]) => { save.reset(); setSaved(null); setDraft(next) }
  const add = () => { const next = withLabel(draft, typed, reported); if (next !== draft) { change(next); setTyped('') } }
  return (
    <InstanceLabelsView
      instance={instance} canEdit={canEdit} draft={draft} typed={typed} busy={save.isPending} saved={saved} failure={save.error}
      onTyped={(text) => { save.reset(); setTyped(text) }} onAdd={add} onRemove={(l) => change(withoutLabel(draft, l))} onSave={() => save.mutate()} onCancel={onClose}
    />
  )
}
