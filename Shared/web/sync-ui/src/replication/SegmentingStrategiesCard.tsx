import { useState } from 'react'
import { CodeEditor, Field } from '@dbdatakit/shell'
import { ParameterForm } from '../components/ParameterForm'
import { useSyncScripts } from '../useSyncDraft'
import { runsAgainstAConnection, type ScriptView, type SegmentingStrategyConfig, type SegmentingStrategyKind } from '../types'

export const STRATEGY_KINDS: { kind: SegmentingStrategyKind; label: string; hint: string }[] = [
  { kind: 'DuckDb', label: 'DuckDB', hint: 'SQL against an in-memory database connected to nothing: it computes the segments from the calendar and reads neither side.' },
  { kind: 'SourceSql', label: 'Source SQL', hint: 'SQL in the source\'s own dialect, run against the source: segments from what the table holds.' },
  { kind: 'TargetSql', label: 'Target SQL', hint: 'The same against the target, for when the answer is kept there already (a control table, a rollup).' },
  { kind: 'Script', label: 'Script', hint: 'A bound C# script, handed both connections, free to use either or neither.' },
]

/** The slot a segmenting script fills. */
export const SEGMENTING_SLOT = 'segmentingStrategy'

const COLUMNS = '1.4fr 1fr 120px'

/** The columns every kind returns, as the starting text of a new strategy. */
export const STARTER_SQL = `-- Every kind returns the same four columns. \`selected\` may be omitted, in
-- which case nothing is pre-selected.
SELECT
    strftime(m, '%Y-%m')                       AS label,
    strftime(m, '%Y-%m-%d')                    AS range_start,
    strftime(m + INTERVAL 1 MONTH, '%Y-%m-%d') AS range_end,
    TRUE                                       AS selected
FROM generate_series(
    DATE '2024-01-01', DATE '2025-01-01', INTERVAL 1 MONTH) AS t(m);`

export const blankStrategy = (): SegmentingStrategyConfig => ({ name: '', kind: 'DuckDb', sql: STARTER_SQL, scriptName: null, parameters: {} })

/** A strategy as the file keeps it: the SQL of a SQL kind, the script and its parameters of a script, never both. */
export function strategyAsWritten(s: SegmentingStrategyConfig): SegmentingStrategyConfig {
  return s.kind === 'Script'
    ? { name: s.name.trim(), kind: s.kind, sql: null, scriptName: s.scriptName ?? null, parameters: s.parameters ?? {} }
    : { name: s.name.trim(), kind: s.kind, sql: s.sql ?? '', scriptName: null, parameters: {} }
}

/**
 * The replication's named segmenting strategies (`segmentingStrategies:` of `task.yaml`), referenced by name from a mapping's Bulk Load tab: a
 * list with an editor below it. Add or Update puts the strategy in the list, which is an unsaved edit of the task until it is saved.
 */
export function SegmentingStrategiesCard({ strategies, onChange, editable = true }: {
  strategies: SegmentingStrategyConfig[]
  onChange: (next: SegmentingStrategyConfig[]) => void
  editable?: boolean
}) {
  const { scripts } = useSyncScripts()
  return <SegmentingStrategiesView strategies={strategies} scripts={scripts} onChange={onChange} editable={editable} />
}

/** The card without its read of the draft's scripts. */
export function SegmentingStrategiesView({ strategies, scripts, onChange, editable = true }: {
  strategies: SegmentingStrategyConfig[]
  scripts: ScriptView[]
  onChange: (next: SegmentingStrategyConfig[]) => void
  editable?: boolean
}) {
  // null is "nothing open"; a number is the index being edited, -1 a new one, so Cancel discards
  const [editing, setEditing] = useState<number | null>(null)
  const [draft, setDraft] = useState<SegmentingStrategyConfig | null>(null)
  const open = (index: number, strategy: SegmentingStrategyConfig) => { setEditing(index); setDraft(structuredClone(strategy)) }
  const close = () => { setEditing(null); setDraft(null) }
  const commit = () => {
    if (!draft) return
    const written = strategyAsWritten(draft)
    onChange(editing === -1 ? [...strategies, written] : strategies.map((s, i) => (i === editing ? written : s)))
    close()
  }
  const taken = draft !== null && strategies.some((s, i) => i !== editing && s.name.trim().toLowerCase() === draft.name.trim().toLowerCase())

  return (
    <div className="card flush" data-testid="segmenting-strategies">
      <div className="card-head tight">
        <span className="card-title sm">Segmenting strategies</span>
        <span className="card-note">
          {strategies.length === 0 ? 'None yet: a named, reusable way of dividing a table for a reload' : `${strategies.length} defined on this replication, referenced by name from its mappings`}
        </span>
        {editable && <button type="button" className="btn btn-sm spacer" onClick={() => open(-1, blankStrategy())} data-testid="add-strategy-button">Add strategy</button>}
      </div>

      <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 10, height: 29 }}><span>Name</span><span>Kind</span><span /></div>
      {strategies.length === 0 && <div className="empty">No strategies defined.</div>}
      {strategies.map((strategy, i) => (
        <div key={i} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 10 }} data-testid={`strategy-${strategy.name}`}>
          <span className="name">{strategy.name}</span>
          <span className="dim">{STRATEGY_KINDS.find((k) => k.kind === strategy.kind)?.label ?? strategy.kind}</span>
          {editable && (
            <span className="row" style={{ gap: 10, justifySelf: 'end' }}>
              <button type="button" className="btn-link" onClick={() => open(i, strategy)} data-testid={`edit-strategy-${strategy.name}`}>Edit</button>
              <button type="button" className="btn-link quiet" onClick={() => onChange(strategies.filter((_, index) => index !== i))} data-testid={`remove-strategy-${strategy.name}`}>Remove</button>
            </span>
          )}
        </div>
      ))}

      {draft && <StrategyEditor draft={draft} setDraft={setDraft} scripts={scripts} onCancel={close} onCommit={commit} isNew={editing === -1} taken={taken} />}
    </div>
  )
}

function StrategyEditor({ draft, setDraft, scripts, onCancel, onCommit, isNew, taken }: {
  draft: SegmentingStrategyConfig
  setDraft: (next: SegmentingStrategyConfig) => void
  scripts: ScriptView[]
  onCancel: () => void
  onCommit: () => void
  isNew: boolean
  taken: boolean
}) {
  const set = (patch: Partial<SegmentingStrategyConfig>) => setDraft({ ...draft, ...patch })
  const available = scripts.filter((s) => s.kind === SEGMENTING_SLOT && s.enabled)
  const script = scripts.find((s) => s.name === draft.scriptName)
  const kind = STRATEGY_KINDS.find((k) => k.kind === draft.kind)

  return (
    <div className="card-body" style={{ borderTop: '1px solid var(--row-edge)' }} data-testid="strategy-editor">
      <div className="form-row">
        <Field label="Name">
          <input className="input" value={draft.name} onChange={(e) => set({ name: e.target.value })} placeholder="by-month" data-testid="strategy-name-input" />
        </Field>
        <Field label="Kind">
          <select className="select" value={draft.kind} onChange={(e) => set({ kind: e.target.value as SegmentingStrategyKind })} data-testid="strategy-kind-select">
            {STRATEGY_KINDS.map((k) => <option key={k.kind} value={k.kind}>{k.label}</option>)}
          </select>
        </Field>
      </div>
      {kind && <span className="hint">{kind.hint}</span>}
      {runsAgainstAConnection(draft.kind) && (
        <span className="hint warn" data-testid="strategy-connection-note">
          This strategy queries the {draft.kind === 'TargetSql' ? 'target' : draft.kind === 'Script' ? 'source or target' : 'source'} database, and does so again on every
          scheduled reload that uses it.
        </span>
      )}

      {draft.kind === 'Script' ? (
        <>
          <Field label="Script">
            <select className="select" value={draft.scriptName ?? ''} onChange={(e) => set({ scriptName: e.target.value || null })} data-testid="strategy-script-select">
              <option value="">Pick a script…</option>
              {draft.scriptName && !available.some((s) => s.name === draft.scriptName) && <option value={draft.scriptName}>{draft.scriptName}, not an enabled segmenting script</option>}
              {available.map((s) => <option key={s.name} value={s.name}>{s.name}</option>)}
            </select>
          </Field>
          {available.length === 0 && (
            <span className="hint" data-testid="strategy-no-script">No enabled script of the draft fills the segmenting slot. Scripts are written in Files, under config/scripts.</span>
          )}
          {script && <ParameterForm parameters={script.parameters} values={draft.parameters ?? {}} onChange={(parameters) => set({ parameters })} testIdPrefix="strategy-parameters" />}
        </>
      ) : (
        <Field label="Query: returns label, range_start, range_end and (optionally) selected">
          <CodeEditor value={draft.sql ?? ''} language="sql" onChange={(sql) => set({ sql })} minLines={8} maxLines={26} testId="strategy-sql-editor" />
        </Field>
      )}

      <div className="row" style={{ gap: 10 }}>
        <button type="button" className="btn btn-sm" disabled title="Trying a strategy reads a database on a Runner, which the Designer does not offer yet." data-testid="try-strategy-button">Try</button>
        <span className="hint" data-testid="try-strategy-note">Trying a strategy reads a database on a Runner, which the Designer does not offer yet.</span>
        <span className="spacer" />
        <button type="button" className="btn btn-sm" onClick={onCancel} data-testid="cancel-strategy-button">Cancel</button>
        <button type="button" className="btn btn-sm btn-primary" disabled={!draft.name.trim() || taken} onClick={onCommit} data-testid="commit-strategy-button">{isNew ? 'Add' : 'Update'}</button>
      </div>
      {taken && <span className="hint" style={{ color: 'var(--danger-ink)' }} data-testid="strategy-name-taken">The replication has a strategy of this name already.</span>}
      <span className="hint">The strategies are kept in the replication's task file: Save settings saves them.</span>
    </div>
  )
}
