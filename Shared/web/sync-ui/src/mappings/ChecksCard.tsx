import { useState } from 'react'
import { CodeEditor, Field } from '@dbdatakit/shell'
import { ColumnMultiPicker } from '../components/ColumnMultiPicker'
import { ParameterForm } from '../components/ParameterForm'
import { useSyncScripts } from '../useSyncDraft'
import type { ScriptView, TableMappingConfig, VerificationCheckConfig, VerificationCheckKind } from '../types'

export const VERIFICATION_KINDS: { kind: VerificationCheckKind; label: string; hint: string }[] = [
  { kind: 'RowCount', label: 'Row count', hint: 'Rows, optionally grouped. The cheapest check and the one that catches the most.' },
  { kind: 'Sum', label: 'Sum', hint: 'Catches what a row count cannot: the right number of rows carrying the wrong values.' },
  { kind: 'Sql', label: 'SQL', hint: 'A statement you write. One for both sides, or one per dialect when the engines differ.' },
  { kind: 'Script', label: 'Script', hint: 'A bound script that makes the statements, for a check that has to read the source first.' },
]

/** The slot a verification script fills. */
export const VERIFICATION_SLOT = 'verificationQueryBuilder'

/** The column that marks a current row, per writer that keeps history: SCD Type 2 has a flag per row, a snapshot target a marker whose newest value is the newest copy. */
export const CURRENT_COLUMN_BY_WRITER: Record<string, string> = { Scd2: 'DS_IsCurrent', Snapshot: 'DS_SnapshotAt' }

const COLUMNS = '1.1fr .7fr 1.6fr 120px'

export function blankCheck(): VerificationCheckConfig {
  return { name: '', kind: 'RowCount', groupBy: [], measures: [], sourceSql: null, targetSql: null, scriptName: null, parameters: {}, filter: null, compareCurrentOnly: false, currentColumn: null, differenceThreshold: 0 }
}

/** What a check compares, in the words the results use. */
export function summarise(check: VerificationCheckConfig): string {
  const measures = check.kind === 'RowCount' ? 'rows' : (check.measures ?? []).join(', ') || 'nothing yet'
  const grouped = (check.groupBy ?? []).length > 0 ? ` by ${check.groupBy.join(', ')}` : ' in total'
  return check.kind === 'Script' ? `${check.scriptName ?? 'no script'}${grouped}` : measures + grouped
}

/** A check as the file keeps it: the keys of its kind only (no measures for a row count, the statements only for SQL, the script only for a script). */
export function checkAsWritten(c: VerificationCheckConfig): VerificationCheckConfig {
  return {
    ...c,
    name: c.name.trim(),
    measures: c.kind === 'RowCount' ? [] : c.measures,
    sourceSql: c.kind === 'Sql' ? c.sourceSql : null,
    targetSql: c.kind === 'Sql' ? c.targetSql : null,
    scriptName: c.kind === 'Script' ? c.scriptName : null,
    parameters: c.kind === 'Script' ? c.parameters : {},
    currentColumn: c.compareCurrentOnly ? c.currentColumn ?? null : null,
  }
}

/**
 * A mapping's verification checks (`verification:` of the mapping): each compares the source and the target, and a Runner runs them and keeps the
 * results. A list with an editor below it; Add or Save puts the check in the mapping, an unsaved edit until the mapping is saved.
 */
export function ChecksCard({ mapping, writerKind, onChange, editable = true }: {
  mapping: TableMappingConfig
  /** The writer the mapping runs, which says whether the target keeps history and what marks a row current. */
  writerKind: string | undefined
  onChange: (checks: VerificationCheckConfig[]) => void
  editable?: boolean
}) {
  const { scripts } = useSyncScripts()
  return <ChecksView mapping={mapping} writerKind={writerKind} scripts={scripts} onChange={onChange} editable={editable} />
}

/** The card without its read of the draft's scripts. */
export function ChecksView({ mapping, writerKind, scripts, onChange, editable = true }: {
  mapping: TableMappingConfig
  writerKind: string | undefined
  scripts: ScriptView[]
  onChange: (checks: VerificationCheckConfig[]) => void
  editable?: boolean
}) {
  const checks = mapping.verification ?? []
  // null is "nothing open"; a number is the index being edited, -1 a new one, so Cancel discards
  const [editing, setEditing] = useState<number | null>(null)
  const [draft, setDraft] = useState<VerificationCheckConfig | null>(null)
  const open = (index: number, check: VerificationCheckConfig) => { setEditing(index); setDraft(structuredClone(check)) }
  const close = () => { setEditing(null); setDraft(null) }
  const commit = () => {
    if (!draft) return
    const written = checkAsWritten(draft)
    onChange(editing === -1 ? [...checks, written] : checks.map((c, i) => (i === editing ? written : c)))
    close()
  }

  return (
    <div className="card flush" data-testid="verification-checks">
      <div className="card-head tight">
        <span className="card-title sm">Checks</span>
        <span className="card-note" data-testid="verification-checks-count">{checks.length === 0 ? 'None yet: a mapping with no checks has nothing to verify' : `${checks.length} on this mapping`}</span>
        {editable && <button type="button" className="btn btn-sm spacer" onClick={() => open(-1, blankCheck())} data-testid="add-check-button">Add check</button>}
      </div>
      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 10, height: 29 }}><span>Name</span><span>Kind</span><span>Compares</span><span /></div>
      {checks.length === 0 && <div className="empty">No checks configured.</div>}
      {checks.map((check, i) => (
        <div key={i} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 10 }} data-testid={`check-${check.name}`} data-kind={check.kind}>
          <span className="name">{check.name}</span>
          <span className="dim">{VERIFICATION_KINDS.find((k) => k.kind === check.kind)?.label ?? check.kind}</span>
          <span className="dim" title={summarise(check)}>{summarise(check)}</span>
          {editable && (
            <span className="row" style={{ gap: 10, justifySelf: 'end' }}>
              <button type="button" className="btn-link" onClick={() => open(i, check)} data-testid={`edit-check-${check.name}`}>Edit</button>
              <button type="button" className="btn-link quiet" onClick={() => onChange(checks.filter((_, index) => index !== i))} data-testid={`remove-check-${check.name}`}>Remove</button>
            </span>
          )}
        </div>
      ))}
      {draft && <CheckEditor mapping={mapping} writerKind={writerKind} scripts={scripts} draft={draft} setDraft={setDraft} onCancel={close} onCommit={commit} isNew={editing === -1}
        taken={checks.some((c, i) => i !== editing && c.name.trim().toLowerCase() === draft.name.trim().toLowerCase())} />}
    </div>
  )
}

function CheckEditor({ mapping, writerKind, scripts, draft, setDraft, onCancel, onCommit, isNew, taken }: {
  mapping: TableMappingConfig
  writerKind: string | undefined
  scripts: ScriptView[]
  draft: VerificationCheckConfig
  setDraft: (next: VerificationCheckConfig) => void
  onCancel: () => void
  onCommit: () => void
  isNew: boolean
  taken: boolean
}) {
  const columns = mapping.columnMappings.map((m) => m.targetColumn)
  const set = (patch: Partial<VerificationCheckConfig>) => setDraft({ ...draft, ...patch })
  const available = scripts.filter((s) => s.kind === VERIFICATION_SLOT && s.enabled)
  const script = scripts.find((s) => s.name === draft.scriptName)
  const currentColumn = writerKind ? CURRENT_COLUMN_BY_WRITER[writerKind] : undefined

  return (
    <div className="card-body" style={{ borderTop: '1px solid var(--row-edge)' }} data-testid="check-editor">
      <div className="form-row">
        <Field label="Name"><input className="input" value={draft.name} placeholder="rows-by-region" onChange={(e) => set({ name: e.target.value })} data-testid="check-name-input" /></Field>
        <Field label="Kind">
          <select className="select" value={draft.kind} onChange={(e) => set({ kind: e.target.value as VerificationCheckKind })} data-testid="check-kind-select">
            {VERIFICATION_KINDS.map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
          </select>
        </Field>
      </div>
      <span className="hint">{VERIFICATION_KINDS.find((k) => k.kind === draft.kind)?.hint}</span>

      <Field label="Group by">
        <ColumnMultiPicker columns={columns} selected={draft.groupBy} onChange={(groupBy) => set({ groupBy })} testId="check-groupby" empty="Ungrouped: one row comparing the whole table." />
      </Field>

      {draft.kind !== 'RowCount' && (
        <Field label="Measures">
          <ColumnMultiPicker columns={columns} selected={draft.measures} onChange={(measures) => set({ measures })} testId="check-measures"
            empty={draft.kind === 'Sum' ? 'A Sum check with no measures compares nothing.' : 'What your statement returns besides the grouping columns.'} />
        </Field>
      )}

      {draft.kind === 'Sql' && (
        <>
          <Field label="Source SQL">
            <CodeEditor value={draft.sourceSql ?? ''} language="sql" onChange={(sourceSql) => set({ sourceSql: sourceSql || null })} minLines={4} testId="check-source-sql" />
          </Field>
          <Field label="Target SQL">
            <span className="hint">Leave it blank to run the source's statement on both sides, when the two engines are close enough for it.</span>
            <CodeEditor value={draft.targetSql ?? ''} language="sql" onChange={(targetSql) => set({ targetSql: targetSql || null })} minLines={4} testId="check-target-sql" />
          </Field>
        </>
      )}

      {draft.kind === 'Script' && (
        <>
          <Field label="Script">
            <select className="select" value={draft.scriptName ?? ''} onChange={(e) => set({ scriptName: e.target.value || null, parameters: {} })} data-testid="check-script-select">
              <option value="">Select…</option>
              {draft.scriptName && !available.some((s) => s.name === draft.scriptName) && <option value={draft.scriptName}>{draft.scriptName}, not an enabled verification script</option>}
              {available.map((s) => <option key={s.name} value={s.name}>{s.name}</option>)}
            </select>
            {available.length === 0 && <span className="hint">No enabled script of the draft fills the verification slot.</span>}
          </Field>
          {script && <ParameterForm parameters={script.parameters} values={draft.parameters} onChange={(parameters) => set({ parameters })} testIdPrefix="check-script-parameters" />}
        </>
      )}

      <Field label="Filter">
        <span className="hint">A SQL predicate narrowing both sides, the shape of the mapping's own source filter.</span>
        <CodeEditor value={draft.filter ?? ''} language="sql" onChange={(filter) => set({ filter: filter || null })} minLines={2} testId="check-filter" />
      </Field>

      {/* only where the writer keeps history: on another target every row is current, and the column would not exist */}
      {currentColumn && (
        <Field label="Compare current rows only">
          <span className="hint">
            {writerKind === 'Snapshot'
              ? 'This target keeps every snapshot, so it holds more rows than the source. Compares against the newest snapshot instead.'
              : 'This target keeps every version of a row, so it holds more rows than the source. Compares against the current version of each.'}
          </span>
          <span className="row" style={{ gap: 7, height: 30 }}>
            <button type="button" className={`toggle ${draft.compareCurrentOnly ? 'on' : ''}`} aria-pressed={draft.compareCurrentOnly ?? false}
              onClick={() => set({ compareCurrentOnly: !draft.compareCurrentOnly, currentColumn: draft.currentColumn ?? currentColumn })} data-testid="check-current-only-toggle" />
            <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>{draft.compareCurrentOnly ? 'On' : 'Off'}</span>
          </span>
        </Field>
      )}
      {draft.compareCurrentOnly && (
        <Field label="Current-row column">
          <input className="input mono" value={draft.currentColumn ?? ''} placeholder={currentColumn ?? ''} onChange={(e) => set({ currentColumn: e.target.value || null })} data-testid="check-current-column-input" />
        </Field>
      )}

      <Field label="Difference threshold (%)">
        <span className="hint">How far apart the two sides may be before it is flagged, as a percentage of the larger side. Zero flags any difference.</span>
        {/* kept as a fraction in the file, shown as a percentage as the results speak of it */}
        <input className="input" type="number" min={0} step="0.01" value={draft.differenceThreshold === 0 ? '0' : String(draft.differenceThreshold * 100)}
          onChange={(e) => set({ differenceThreshold: (Number(e.target.value) || 0) / 100 })} data-testid="check-threshold-input" />
      </Field>

      <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
        {taken && <span className="hint" style={{ color: 'var(--danger-ink)' }} data-testid="check-name-taken">The mapping has a check of this name already.</span>}
        <button type="button" className="btn" onClick={onCancel} data-testid="cancel-check-button">Cancel</button>
        <button type="button" className="btn btn-primary" disabled={!draft.name.trim() || taken} onClick={onCommit} data-testid="save-check-button">{isNew ? 'Add check' : 'Save check'}</button>
      </div>
    </div>
  )
}
