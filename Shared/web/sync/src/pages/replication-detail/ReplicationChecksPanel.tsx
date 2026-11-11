import { useState } from 'react'
import { ErrorBanner, Field, useIsAdmin } from '@dbdatakit/shell'
import { useReplicationChecks, useSaveReplicationChecks } from '../../api/hooks'
import type { ChecksFault, CheckForm } from '../../api/types'
import { CHECK_KINDS, blankDraft, currentOf, draftOf, faultsOf, kindInfo, sameChecks, saveBlockedReason, summary, wire, type CheckDraft } from './checkForms'

const COLUMNS = '1.2fr .8fr 2.4fr .7fr 150px'

/**
 * The quality checks stored with a replication (`checks:` of its `task.yaml`, plan 23a FQ04; this screen is plan 33 UE07): list, add, edit and remove checks of the seven kinds with their severity and what happens when a check cannot run. They are
 * evaluated after a run and on a clock (FQ10) and their results are on the console's Quality page; the mapping's verification checks (Monitoring) compare source and target and are a different thing.
 * <para>
 * Changes are a draft until saved: Save writes the whole section in one commit, and the server reads the file again with the same strict reader that loads it, so a fault comes back with its file, line and a suggestion and nothing is written.
 * The screen judges nothing itself. A section changed by somebody else since it was read is refused (not overwritten) and says so.
 * </para>
 */
export function ReplicationChecksPanel({ replicationName }: { replicationName: string }) {
  const isAdmin = useIsAdmin()
  const query = useReplicationChecks(replicationName)
  const save = useSaveReplicationChecks(replicationName)
  // null is "what the server has"; a list is an unsaved edit of it
  const [draft, setDraft] = useState<CheckForm[] | null>(null)
  // null is "nothing open"; an index is the check being edited, -1 a new one
  const [editing, setEditing] = useState<{ index: number; draft: CheckDraft } | null>(null)

  const read = query.data
  const checks = draft ?? read?.checks ?? []
  const dirty = draft !== null && read !== undefined && !sameChecks(draft, read.checks)
  const blocked = saveBlockedReason(isAdmin, dirty, save.isPending)
  const refused = faultsOf(save.error)
  const conflict = currentOf(save.error)
  const loadFaults = faultsOf(query.error)
  const datasetGuess = checks[0]?.dataset ?? ''

  const apply = () => {
    if (!editing) return
    const next = wire(editing.draft)
    setDraft(editing.index === -1 ? [...checks, next] : checks.map((c, i) => (i === editing.index ? next : c)))
    setEditing(null)
  }
  const remove = (index: number) => setDraft(checks.filter((_, i) => i !== index))
  const discard = () => { setDraft(null); setEditing(null); save.reset() }
  const submit = async () => {
    if (!read || !draft) return
    try { await save.mutateAsync({ checks: draft, version: read.version }); setDraft(null) } catch { /* shown below, in the server's words */ }
  }
  const reload = async () => { save.reset(); setDraft(null); await query.refetch() }

  return (
    <div className="pane" data-testid="replication-checks">
      <div className="card flush">
        <div className="card-head tight">
          <span className="card-title sm">Quality checks</span>
          <span className="card-note" data-testid="replication-checks-count">
            {query.isLoading ? 'Reading…' : checks.length === 0 ? 'None yet: this replication has nothing to evaluate' : `${checks.length} on this replication`}
          </span>
          <button type="button" className="btn btn-sm spacer" disabled={!isAdmin || !read} title={!isAdmin ? 'Adding a check needs the admin role.' : undefined}
            onClick={() => setEditing({ index: -1, draft: blankDraft('freshness', datasetGuess) })} data-testid="replication-check-add">
            Add check
          </button>
        </div>

        {query.isLoading && <div className="empty" data-testid="replication-checks-loading">Reading the checks…</div>}
        {loadFaults.length > 0
          ? <FaultList faults={loadFaults} testId="replication-checks-load-faults" intro="The checks section of this replication's task file cannot be read. Fix the file (it is in version control), then reload." />
          : <ErrorBanner error={query.error} />}

        {read && <ChecksTable checks={checks} canEdit={isAdmin} onEdit={(i) => setEditing({ index: i, draft: draftOf(checks[i]) })} onRemove={remove} />}

        {editing && <CheckEditor draft={editing.draft} setDraft={(d) => setEditing({ ...editing, draft: d })} isNew={editing.index === -1} onApply={apply} onCancel={() => setEditing(null)} />}

        {read?.hasComments && (
          <div className="hint" style={{ padding: '8px 14px' }} data-testid="replication-checks-comments">
            The section in the file has comments. Saving rewrites the section from this list and does not keep the comments inside it (the rest of the file stays as it is).
          </div>
        )}

        {refused.length > 0 && <FaultList faults={refused} testId="replication-checks-diagnostics" intro="The checks were not saved: the file would not read." />}
        {conflict && (
          <div className="banner" role="alert" style={{ padding: '8px 14px' }} data-testid="replication-checks-conflict">
            The checks were changed after you read them (somebody saved, or edited the file). Nothing was overwritten.{' '}
            <button type="button" className="btn btn-sm" onClick={() => void reload()} data-testid="replication-checks-reload">Load the current checks</button>
          </div>
        )}
        {!conflict && refused.length === 0 && <ErrorBanner error={save.error} />}

        <div className="card-body row" style={{ gap: 10, borderTop: '1px solid var(--row-edge)' }}>
          <button type="button" className="btn btn-primary" disabled={blocked !== null} title={blocked ?? undefined} onClick={() => void submit()} data-testid="replication-checks-save">
            {save.isPending ? 'Saving…' : 'Save checks'}
          </button>
          <button type="button" className="btn" disabled={draft === null} onClick={discard} data-testid="replication-checks-discard">Discard changes</button>
          {blocked && !save.isPending && <span className="hint" data-testid="replication-checks-save-why">{blocked}</span>}
          {save.isSuccess && draft === null && <span className="hint" role="status" data-testid="replication-checks-saved">Saved and committed.</span>}
        </div>
      </div>
    </div>
  )
}

/** The list of checks (presentational: the panel owns the draft). */
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
            <button type="button" className="btn-link" onClick={() => onEdit(i)} disabled={!canEdit} title={canEdit ? undefined : 'Editing a check needs the admin role.'} data-testid={`replication-check-edit-${check.id}`}>Edit</button>
            <button type="button" className="btn-link quiet" onClick={() => onRemove(i)} disabled={!canEdit} title={canEdit ? undefined : 'Removing a check needs the admin role.'} data-testid={`replication-check-remove-${check.id}`}>Remove</button>
          </span>
        </div>
      ))}
    </>
  )
}

/** The faults the strict reader found, each with its file, line and what to do. */
export function FaultList({ faults, testId, intro }: { faults: ChecksFault[]; testId: string; intro: string }) {
  return (
    <div className="banner" role="alert" style={{ padding: '8px 14px' }} data-testid={testId}>
      {intro}
      <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
        {faults.map((f, i) => (
          <li key={i} data-testid="replication-checks-fault" data-code={f.code}>
            <span className="mono">{f.file}{f.line > 0 ? `:${f.line}` : ''}</span> {f.message}{f.fix ? ` (${f.fix})` : ''}
          </li>
        ))}
      </ul>
    </div>
  )
}

function CheckEditor({ draft, setDraft, isNew, onApply, onCancel }: { draft: CheckDraft; setDraft: (next: CheckDraft) => void; isNew: boolean; onApply: () => void; onCancel: () => void }) {
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
        <button type="button" className="btn btn-primary" onClick={onApply} data-testid="replication-check-apply">{isNew ? 'Add to the list' : 'Apply to the list'}</button>
        <button type="button" className="btn" onClick={onCancel} data-testid="replication-check-cancel">Cancel</button>
        <span className="hint">The list is saved with Save checks.</span>
      </div>
    </div>
  )
}
