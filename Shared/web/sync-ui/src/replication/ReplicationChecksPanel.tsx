import { useState } from 'react'
import { Field } from '@dbdatakit/shell'
import type { CheckForm } from '../types'
import { CHECK_KINDS, applyBlockedReason, blankDraft, draftOf, kindInfo, summary, wire, type CheckDraft } from './checkForms'

const COLUMNS = '1.2fr .8fr 2.4fr .7fr 150px'

/**
 * The quality checks stored with a replication (`checks:` of its `task.yaml`): list, add, edit and remove checks of the seven kinds with their
 * severity and what happens when a check cannot run. They are evaluated after a run and on a clock, and their results are on the console's Quality
 * page; a mapping's verification checks compare source and target and are another thing.
 *
 * Add or Apply puts the check in the list, which is an unsaved edit of the task file like every other edit on the page (Save settings saves it).
 * The screen judges nothing itself: the draft's check reads the section with the strict reader a Runner loads it with, and names the file and line
 * of a fault.
 */
export function ReplicationChecksPanel({ checks, onChange, editable = true }: {
  checks: CheckForm[]
  onChange: (next: CheckForm[]) => void
  editable?: boolean
}) {
  // null is "nothing open"; an index is the check being edited, -1 a new one
  const [editing, setEditing] = useState<{ index: number; draft: CheckDraft } | null>(null)
  const datasetGuess = checks[0]?.dataset ?? ''
  const blocked = editing ? applyBlockedReason(editing.draft, checks, editing.index) : null

  const apply = () => {
    if (!editing || blocked) return
    const next = wire(editing.draft)
    onChange(editing.index === -1 ? [...checks, next] : checks.map((c, i) => (i === editing.index ? next : c)))
    setEditing(null)
  }

  return (
    <div className="pane" data-testid="replication-checks">
      <div className="card flush">
        <div className="card-head tight">
          <span className="card-title sm">Quality checks</span>
          <span className="card-note" data-testid="replication-checks-count">{checks.length === 0 ? 'None yet: this replication has nothing to evaluate' : `${checks.length} on this replication`}</span>
          {editable && (
            <button type="button" className="btn btn-sm spacer" onClick={() => setEditing({ index: -1, draft: blankDraft('freshness', datasetGuess ?? '') })} data-testid="replication-check-add">Add check</button>
          )}
        </div>

        <ChecksTable checks={checks} canEdit={editable} onEdit={(i) => setEditing({ index: i, draft: draftOf(checks[i]!) })} onRemove={(i) => onChange(checks.filter((_, index) => index !== i))} />

        {editing && (
          <CheckEditor draft={editing.draft} setDraft={(d) => setEditing({ ...editing, draft: d })} isNew={editing.index === -1} blocked={blocked} onApply={apply} onCancel={() => setEditing(null)} />
        )}

        <div className="hint" style={{ padding: '8px 14px' }}>The checks are kept in the replication's task file: Save settings saves them, and the draft's check reads them.</div>
      </div>
    </div>
  )
}

/** The list of checks (the panel owns the list). */
export function ChecksTable({ checks, canEdit, onEdit, onRemove }: { checks: CheckForm[]; canEdit: boolean; onEdit: (index: number) => void; onRemove: (index: number) => void }) {
  if (checks.length === 0) return <div className="empty" data-testid="replication-checks-empty">No checks configured.</div>
  return (
    <>
      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 10, height: 29 }}>
        <span>Id</span><span>Kind</span><span>What it checks</span><span>Severity</span><span />
      </div>
      {checks.map((check, i) => (
        <div key={`${check.id}-${i}`} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 10 }} data-testid={`replication-check-${check.id}`} data-kind={check.kind} data-severity={check.severity}>
          <span className="name">{check.id}</span>
          <span className="dim">{kindInfo(check.kind)?.label ?? check.kind}</span>
          <span className="dim" title={summary(check)}>{summary(check)}</span>
          <span className="dim">{check.severity}{check.on_error === 'block' ? ' · blocks on error' : ''}</span>
          <span className="row" style={{ gap: 10, justifySelf: 'end' }}>
            <button type="button" className="btn-link" onClick={() => onEdit(i)} disabled={!canEdit} title={canEdit ? undefined : 'Editing a check needs the author role on this draft.'} data-testid={`replication-check-edit-${check.id}`}>Edit</button>
            <button type="button" className="btn-link quiet" onClick={() => onRemove(i)} disabled={!canEdit} title={canEdit ? undefined : 'Removing a check needs the author role on this draft.'} data-testid={`replication-check-remove-${check.id}`}>Remove</button>
          </span>
        </div>
      ))}
    </>
  )
}

function CheckEditor({ draft, setDraft, isNew, blocked, onApply, onCancel }: {
  draft: CheckDraft; setDraft: (next: CheckDraft) => void; isNew: boolean; blocked: string | null; onApply: () => void; onCancel: () => void
}) {
  const info = kindInfo(draft.kind)
  const fields = info?.fields ?? []
  const set = (patch: Partial<CheckDraft>) => setDraft({ ...draft, ...patch })
  const input = (field: keyof CheckDraft, label: string, placeholder?: string) => (
    <Field label={label}>
      <input className="input" value={draft[field]} placeholder={placeholder} onChange={(e) => set({ [field]: e.target.value } as Partial<CheckDraft>)} data-testid={`replication-check-${field}-input`} />
    </Field>
  )
  const columnsLabel = fields.includes('max_age_column') ? 'Column (optional)' : 'Column (several: separate with commas)'
  return (
    <div className="card-body" style={{ borderTop: '1px solid var(--row-edge)' }} data-testid="replication-check-editor">
      <div className="form-row">
        <Field label="Id">
          <input className="input" value={draft.id} placeholder="orders_fresh" disabled={!isNew} title={isNew ? undefined : 'A check keeps its id: its results and its block are kept by it.'} onChange={(e) => set({ id: e.target.value })} data-testid="replication-check-id-input" />
        </Field>
        <Field label="Kind">
          <select className="select" value={draft.kind} onChange={(e) => set({ kind: e.target.value })} data-testid="replication-check-kind-select">
            {CHECK_KINDS.map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
          </select>
        </Field>
        {input('dataset', 'Dataset', 'dbo.orders')}
      </div>
      <span className="hint" data-testid="replication-check-kind-hint">{info?.hint}</span>
      <div className="form-row">
        {(fields.includes('columns') || fields.includes('max_age_column')) && input('columns', columnsLabel, 'order_id')}
        {fields.includes('max_age') && input('maxAge', 'Oldest the data may be', '6h')}
        {fields.includes('min') && input('min', 'Minimum', '0')}
        {fields.includes('max') && input('max', 'Maximum', '1000')}
        {fields.includes('max_increase_percent') && input('maxIncrease', 'Largest increase (%)', '50')}
        {fields.includes('max_decrease_percent') && input('maxDecrease', 'Largest decrease (%)', '20')}
      </div>
      {fields.includes('values') && (
        <Field label="Accepted values (one per line)">
          <textarea className="input" rows={3} value={draft.values} onChange={(e) => set({ values: e.target.value })} data-testid="replication-check-values-input" />
        </Field>
      )}
      {fields.includes('references') && (
        <div className="form-row">
          {input('referencesDataset', 'Referenced dataset', 'dbo.customers')}
          {input('referencesColumns', 'Referenced column (several: separate with commas)', 'id')}
        </div>
      )}
      <div className="form-row">
        <Field label="When it fails">
          <select className="select" value={draft.severity} onChange={(e) => set({ severity: e.target.value })} data-testid="replication-check-severity-select">
            <option value="warn">warn: tell the owner</option>
            <option value="block">block: stop what reads this dataset</option>
          </select>
        </Field>
        <Field label="When it cannot run">
          <select className="select" value={draft.onError} onChange={(e) => set({ onError: e.target.value })} data-testid="replication-check-on-error-select">
            <option value="alert">alert: tell the owner, do not block</option>
            <option value="block">block</option>
          </select>
        </Field>
      </div>
      {input('description', 'Description (optional)')}
      <div className="row" style={{ gap: 10, marginTop: 10 }}>
        <button type="button" className="btn btn-primary" onClick={onApply} disabled={blocked !== null} title={blocked ?? undefined} data-testid="replication-check-apply">{isNew ? 'Add to the list' : 'Apply to the list'}</button>
        <button type="button" className="btn" onClick={onCancel} data-testid="replication-check-cancel">Cancel</button>
        {blocked && <span className="hint" data-testid="replication-check-blocked">{blocked}</span>}
      </div>
    </div>
  )
}
