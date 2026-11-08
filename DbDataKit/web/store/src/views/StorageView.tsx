import type { CopyState, MigrationPlan, Statement, StoragePlan } from '../api'
import { copyAction, migrationSummary, statusText } from '../model'
import { Gated } from './ModelEditorView'

export interface CopyViewProps {
  /** `default` is the model's own copy. */
  name: string
  schema?: string | null
  connection?: string | null
  plan: StoragePlan | undefined
  planError?: string | null
  state: CopyState | undefined
  /** What the last dry run of a migration said, if one was run in this view. */
  dry: MigrationPlan | null
  /** What the last action did, in a sentence. */
  done: string | null
  refusal: string | null
  /** The reason every change is off (a role, an invalid model), or null. */
  blocked: string | null
  busy: boolean
  onCreate: () => void
  onDryRun: () => void
  onApply: () => void
}

function Statements({ statements, testId }: { statements: Statement[]; testId: string }) {
  if (statements.length === 0) return null
  return (
    <details data-testid={testId} style={{ marginTop: 8 }}>
      <summary>{statements.length} statement{statements.length === 1 ? '' : 's'}</summary>
      {statements.map((s, i) => (
        <div key={i} style={{ margin: '6px 0' }} data-testid={`${testId}-item`}>
          <div className="hint">{s.description}</div>
          <pre className="mono" style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{s.text}</pre>
        </div>
      ))}
    </details>
  )
}

/** One copy of the model (its own, or an instance): what is in the database, what the model needs, and the three steps: create the tables, dry-run a migration, apply it. */
export function CopyView(p: CopyViewProps) {
  const next = copyAction(p.plan, p.state)
  const summary = p.dry ? migrationSummary(p.dry) : null
  const why = (extra: string | null) => p.blocked ?? extra
  return (
    <div className="card" data-testid="storage-copy" data-copy={p.name} data-action={next.action}>
      <div className="card-body">
        <div className="row" style={{ gap: 12, alignItems: 'baseline', flexWrap: 'wrap' }}>
          <div className="card-title mono">{p.name === 'default' ? 'The model\'s own copy' : `Instance ${p.name}`}</div>
          {p.plan && <span className="hint" data-testid="storage-engine">{p.plan.engine}{p.schema ? `, schema ${p.schema}` : ''}{p.connection ? `, connection ${p.connection}` : ''}</span>}
          {p.state && <span className="hint" data-testid="storage-status" data-status={p.state.status}>{statusText(p.state.status)}</span>}
        </div>
        {p.planError && <div className="banner error" role="alert" data-testid="storage-error"><span className="mark">!</span><span>{p.planError}</span></div>}
        {p.plan?.existing === null && <div className="banner error" role="alert" data-testid="storage-connection-problem"><span className="mark">!</span><span>{p.plan.connection_problem}</span></div>}
        {p.plan && p.plan.existing !== null && (
          <p data-testid="storage-tables">{p.plan.existing.length === 0 ? 'No tables of this model are in the database yet.' : `In the database: ${p.plan.existing.join(', ')}.`}</p>
        )}
        <p className="hint" data-testid="storage-next">{next.text}</p>
        {p.plan && next.action === 'create' && <Statements statements={p.plan.statements} testId="storage-create-statements" />}
        <div className="row" style={{ gap: 14, flexWrap: 'wrap', marginTop: 10 }}>
          {next.action === 'create' && <Gated testId="storage-create" label="Create tables" className="btn btn-primary" allowed={!p.blocked && !p.busy} reason={why(p.busy ? 'Working...' : null)} onClick={p.onCreate} />}
          {next.action !== 'create' && p.plan?.existing !== null && <Gated testId="storage-dry-run" label="Dry run a migration" allowed={!p.blocked && !p.busy} reason={why(p.busy ? 'Working...' : null)} onClick={p.onDryRun} />}
          {summary && <Gated testId="storage-apply" label="Apply the migration" className="btn btn-primary" allowed={!p.blocked && summary.canApply && !p.busy} reason={why(summary.canApply ? (p.busy ? 'Working...' : null) : 'There is nothing that can be applied.')} onClick={p.onApply} />}
        </div>
        {p.dry && summary && (
          <div style={{ marginTop: 10 }} data-testid="storage-dry-result" data-applied={p.dry.applied}>
            <p data-testid="storage-dry-summary">{summary.text}</p>
            <Statements statements={p.dry.statements} testId="storage-dry-statements" />
            {p.dry.blocked.length > 0 && <ul data-testid="storage-blocked">{p.dry.blocked.map((b) => <li key={b}>{b}</li>)}</ul>}
            {p.dry.notes.length > 0 && <ul className="hint" data-testid="storage-notes">{p.dry.notes.map((n) => <li key={n}>{n}</li>)}</ul>}
          </div>
        )}
        {p.done && <div className="banner" role="status" data-testid="storage-done">{p.done}</div>}
        {p.refusal && <div className="banner error" role="alert" data-testid="storage-refusal"><span className="mark">!</span><span>{p.refusal}</span></div>}
      </div>
    </div>
  )
}
