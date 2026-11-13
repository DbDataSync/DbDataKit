import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useAuthStatus, useIsAdmin } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import { columnRuleText, coverageText, explainQuery, explainReady, isReadsOnly, ruleText, rotateWarning, stateLabel, tableAgeText, uncoveredTotal, valueText, valuesText } from '../access'
import type { AccessCoverage, AccessDefinition, AccessExplainedDataset, AccessExplanation, AccessRulesAnswer, ExplainForm } from '../accessTypes'
import { GatewayShell } from '../GatewayShell'

const wrap = { whiteSpace: 'normal', overflowWrap: 'anywhere' } as const
const emptyForm = (subject = ''): ExplainForm => ({ subject, kind: 'person', dataset: '', row: [{ column: '', value: '' }] })

export interface AccessViewProps {
  data: AccessRulesAnswer | undefined
  error: unknown
  isAdmin: boolean
  /** The name of the person looking, for a viewer's explanation of themselves. */
  me: string
  /** The definition whose explain form and coverage are open. */
  opened: string | null
  form: ExplainForm
  explanation: AccessExplanation | undefined
  explainError: unknown
  explaining: boolean
  coverage: AccessCoverage | undefined
  coverageError: unknown
  checking: boolean
  /** The mask key's rotate button waits for a second click. */
  confirmingRotate: boolean
  rotated: string | null
  rotateBusy: boolean
  now?: Date
  onOpen: (reference: string | null) => void
  onForm: (form: ExplainForm) => void
  onExplain: () => void
  onCoverage: () => void
  onRotate: () => void
}

function Badge({ state, testId }: { state: string; testId: string }) {
  return <span className="badge" data-testid={testId} data-state={state} style={{ fontWeight: 600 }}>{state}</span>
}

/** Every warning of a definition; the one that says row rules limit reads only is always shown, in a banner (plan 22a QR6, the owner requires it to be visible). */
function Warnings({ warnings, testId }: { warnings: string[]; testId: string }) {
  if (warnings.length === 0) return null
  return (
    <div style={{ margin: '8px 0' }} data-testid={testId}>
      {warnings.map((w, i) => (
        <div key={i} className={isReadsOnly(w) ? 'banner' : 'hint'} role={isReadsOnly(w) ? 'note' : undefined} style={{ marginBottom: 4, ...wrap }} data-testid={isReadsOnly(w) ? 'access-reads-only' : 'access-warning'}>{w}</div>
      ))}
    </div>
  )
}

function Tables({ definition }: { definition: AccessDefinition }) {
  const tables = definition.tables ?? []
  if (tables.length === 0) return null
  return (
    <div className="card flush" style={{ margin: '6px 0 10px' }} data-testid={`access-tables-${definition.name}`}>
      <div className="grid-head" style={{ gridTemplateColumns: '2fr 3fr 4fr', gap: 14 }}><span>Rules table</span><span>State</span><span>Age</span></div>
      {tables.map((t) => (
        <div key={t.table} className="grid-row auto" style={{ gridTemplateColumns: '2fr 3fr 4fr', gap: 14 }} data-testid={`access-table-${definition.name}-${t.table}`}>
          <span className="mono" style={wrap}>{t.table}</span>
          <span style={wrap}><Badge state={t.state} testId={`access-table-state-${definition.name}-${t.table}`} /> <span className="hint">{stateLabel(t.state)}</span></span>
          <span className="hint" style={wrap}>{tableAgeText(t)}</span>
        </div>
      ))}
    </div>
  )
}

function RowRules({ definition }: { definition: AccessDefinition }) {
  const rules = definition.row ?? []
  return (
    <div data-testid={`access-row-rules-${definition.name}`}>
      <h3 className="card-title" style={{ marginTop: 12 }}>Row rules</h3>
      {rules.length === 0 && <p className="hint" data-testid={`access-row-none-${definition.name}`}>No row rule: every row of every dataset is read as it is.</p>}
      {rules.map((d) => (
        <div key={d.dataset} className="card" style={{ margin: '6px 0' }} data-testid={`access-dataset-${definition.name}-${d.dataset}`}>
          <div className="card-body">
            <strong className="mono">{d.dataset}</strong> <span className="hint">groups combine with {d.match === 'all' ? 'all of them' : 'any one of them'}; a person with no matching include rule reads no rows</span>
            {d.groups.map((g) => (
              <div key={g.id} style={{ marginTop: 6 }}>
                <div className="hint">Group <span className="mono">{g.id}</span>: include rules combine with {g.match === 'all' ? 'all' : 'any'}; remove rules always subtract</div>
                {g.rules.map((r) => (
                  <div key={r.id} style={{ marginLeft: 12, ...wrap }} data-testid={`access-rule-${definition.name}-${d.dataset}-${r.id}`} data-effect={r.effect}>
                    <span className="mono">{r.id}</span>: {ruleText(r)}{r.max_age_seconds ? ` (rules table at most ${r.max_age_seconds / 60} min old)` : ''}
                  </div>
                ))}
              </div>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}

function ColumnRules({ definition }: { definition: AccessDefinition }) {
  const rules = definition.columns ?? []
  return (
    <div data-testid={`access-column-rules-${definition.name}`}>
      <h3 className="card-title" style={{ marginTop: 12 }}>Column rules</h3>
      {rules.length === 0 && <p className="hint">No column rule: every column is read as it is.</p>}
      {rules.length > 0 && (
        <div className="card flush">
          {rules.map((c, i) => (
            <div key={i} className="grid-row auto" style={{ gridTemplateColumns: '2fr 4fr', gap: 14 }} data-testid={`access-column-rule-${definition.name}-${c.dataset}-${c.column}-${i}`}>
              <span className="mono">{c.dataset}.{c.column}</span><span>{columnRuleText(c)}</span>
            </div>
          ))}
          <div className="hint" style={{ padding: '6px 12px' }}>When several rules name one person, the strongest wins: hide, then null, then mask.</div>
        </div>
      )}
    </div>
  )
}

function DatasetExplanation({ d }: { d: AccessExplainedDataset }) {
  return (
    <div className="card" style={{ margin: '8px 0' }} data-testid={`access-explained-${d.dataset}`} data-refused={d.refusal ? 'true' : 'false'}>
      <div className="card-body">
        <strong className="mono">{d.dataset}</strong>
        <p style={wrap} data-testid={`access-summary-${d.dataset}`}>{d.summary}</p>
        {d.verdict && (
          <div className={d.verdict.included ? 'hint' : 'banner'} style={wrap} data-testid={`access-verdict-${d.dataset}`} data-included={d.verdict.included ? 'true' : 'false'} data-decided-by={d.verdict.decided_by.join(' ')}>
            <strong>{d.verdict.included ? 'Included' : 'Not included'}</strong> ({valuesText(d.verdict.row)}): {d.verdict.text}
          </div>
        )}
        {d.missing_columns.length > 0 && <p className="hint" data-testid={`access-missing-${d.dataset}`}>To judge a row, give its {d.missing_columns.join(', ')}.</p>}
        {d.groups.map((g) => g.rules.map((r) => (
          <div key={g.id + r.id} style={{ marginTop: 6 }} data-testid={`access-explained-rule-${d.dataset}-${g.id}-${r.id}`} data-holds={r.holds === null ? '' : r.holds ? 'true' : 'false'}>
            <div style={wrap}><span className="mono">{g.id}/{r.id}</span> ({r.effect}, {r.table}): {r.names.length === 0 ? 'no row of the table names them' : `${r.rows.length}${r.rows_truncated ? '+' : ''} ${r.rows.length === 1 ? 'row names' : 'rows name'} them as ${r.names.join(', ')}`}{r.holds === null ? '' : r.holds ? ', holds for the row' : ', does not hold for the row'}</div>
            {r.read_problem && <div className="banner error" role="alert" style={wrap}>{r.read_problem}</div>}
            {r.rows.length > 0 && (
              <div className="card flush" style={{ margin: '4px 0 4px 12px' }} data-testid={`access-explained-rows-${d.dataset}-${g.id}-${r.id}`}>
                {r.rows.map((row, i) => <div key={i} className="grid-row auto" style={{ gridTemplateColumns: '1fr 4fr', gap: 14 }}><span className="mono">{row.subject}</span><span style={wrap}>{valuesText(row.values)}</span></div>)}
              </div>
            )}
          </div>
        )))}
      </div>
    </div>
  )
}

function ExplainPanel(p: AccessViewProps & { definition: AccessDefinition }) {
  const { definition, form } = p
  const datasets = (definition.row ?? []).map((r) => r.dataset)
  const set = (patch: Partial<ExplainForm>) => p.onForm({ ...form, ...patch })
  const asSelf = !p.isAdmin
  return (
    <div data-testid={`access-explain-${definition.name}`}>
      <h3 className="card-title" style={{ marginTop: 12 }}>Why can or can’t they see this?</h3>
      {asSelf && <p className="hint" data-testid="access-explain-self">You can ask about yourself. An administrator can ask about anyone.</p>}
      <form onSubmit={(e) => { e.preventDefault(); p.onExplain() }}>
        <div className="row" style={{ gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          <div className="field">
            <label htmlFor="access-kind">Who</label>
            <select id="access-kind" className="input" value={form.kind} disabled={asSelf} onChange={(e) => set({ kind: e.target.value as ExplainForm['kind'] })} data-testid="access-kind">
              <option value="person">A person</option>
              <option value="group">A group</option>
            </select>
          </div>
          <div className="field">
            <label htmlFor="access-subject">{form.kind === 'group' ? 'Group name' : 'Person (name, email or id)'}</label>
            <input id="access-subject" className="input" value={form.subject} readOnly={asSelf} onChange={(e) => set({ subject: e.target.value })} data-testid="access-subject" />
          </div>
          <div className="field">
            <label htmlFor="access-dataset">Dataset</label>
            <select id="access-dataset" className="input" value={form.dataset} onChange={(e) => set({ dataset: e.target.value })} data-testid="access-dataset">
              <option value="">All datasets with rules</option>
              {datasets.map((d) => <option key={d} value={d}>{d}</option>)}
            </select>
          </div>
        </div>
        <div style={{ marginTop: 8 }}>
          <div className="hint">A row to ask about (optional): the dataset’s column and its value; write <span className="mono">null</span> for NULL.</div>
          {form.row.map((r, i) => (
            <div key={i} className="row" style={{ gap: 8, marginTop: 4 }}>
              <input className="input" placeholder="column" aria-label="Row column" value={r.column} onChange={(e) => set({ row: form.row.map((x, j) => (j === i ? { ...x, column: e.target.value } : x)) })} data-testid={`access-row-column-${i}`} />
              <input className="input" placeholder="value" aria-label="Row value" value={r.value} onChange={(e) => set({ row: form.row.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)) })} data-testid={`access-row-value-${i}`} />
            </div>
          ))}
          <button type="button" className="btn sm" style={{ marginTop: 4 }} onClick={() => set({ row: [...form.row, { column: '', value: '' }] })} data-testid="access-row-add">Add a column</button>
        </div>
        <div style={{ marginTop: 10 }}>
          <button type="submit" className="btn primary" disabled={!explainReady(form) || p.explaining} data-testid="access-explain-run">{p.explaining ? 'Asking…' : 'Explain'}</button>
        </div>
      </form>
      <ErrorBanner error={p.explainError} />
      {p.explanation && p.explanation.definition === definition.reference && (
        <div style={{ marginTop: 12 }} data-testid="access-explain-result" data-subject={p.explanation.subject} data-fingerprint={p.explanation.fingerprint}>
          <p style={wrap} data-testid="access-explain-names">The rules see {p.explanation.subject} ({p.explanation.subject_kind}) as: {p.explanation.names.length === 0 ? 'no name' : p.explanation.names.join(', ')}</p>
          <Warnings warnings={p.explanation.warnings} testId="access-explain-warnings" />
          {p.explanation.datasets.map((d) => <DatasetExplanation key={d.dataset} d={d} />)}
          {p.explanation.columns.length > 0 && (
            <div data-testid="access-explain-columns">
              <h4 className="card-title">Columns</h4>
              {p.explanation.columns.map((c) => <div key={c.dataset + c.column} data-testid={`access-explained-column-${c.dataset}-${c.column}`} data-treatment={c.treatment}><span className="mono">{c.dataset}.{c.column}</span>: {c.treatment} <span className="hint">({c.rules.length} {c.rules.length === 1 ? 'rule' : 'rules'} name them; the strongest wins)</span></div>)}
            </div>
          )}
          <div className="card flush" style={{ margin: '8px 0' }} data-testid="access-explain-tables">
            {p.explanation.tables.map((t) => (
              <div key={t.table} className="grid-row auto" style={{ gridTemplateColumns: '2fr 3fr 4fr', gap: 14 }} data-testid={`access-explained-table-${t.table}`} data-state={t.state}>
                <span className="mono">{t.table}</span><span>{stateLabel(t.state)}</span><span className="hint">{tableAgeText(t)}</span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

function CoveragePanel(p: AccessViewProps & { definition: AccessDefinition }) {
  const report = p.coverage && p.coverage.definition === p.definition.reference ? p.coverage : undefined
  return (
    <div data-testid={`access-coverage-${p.definition.name}`}>
      <h3 className="card-title" style={{ marginTop: 12 }}>Coverage</h3>
      <p className="hint">The values of each filtered dataset that no row of a rule’s rules table covers, NULL counted. Nobody a rule would include reads those rows.</p>
      <button type="button" className="btn" disabled={p.checking} onClick={p.onCoverage} data-testid="access-coverage-run">{p.checking ? 'Checking…' : 'Check coverage'}</button>
      <ErrorBanner error={p.coverageError} />
      {report && (
        <div style={{ marginTop: 8 }} data-testid="access-coverage-result" data-uncovered={uncoveredTotal(report.rules)}>
          {report.rules.length === 0 && <p className="hint">No rule to check.</p>}
          {report.rules.map((r) => (
            <div key={`${r.dataset}/${r.group}/${r.rule}`} className="card" style={{ margin: '6px 0' }} data-testid={`access-coverage-rule-${r.dataset}-${r.rule}`} data-uncovered={r.uncovered} data-problem={r.problem ? 'true' : 'false'}>
              <div className="card-body">
                <span className="mono">{r.dataset}</span> / <span className="mono">{r.group}/{r.rule}</span> ({r.effect}, {r.table}): <span style={wrap}>{coverageText(r)}</span>
                {r.values.map((v, i) => (
                  <div key={i} style={{ marginLeft: 12, ...wrap }} data-testid={`access-coverage-value-${r.dataset}-${r.rule}-${i}`}>
                    {Object.entries(v.values).map(([k, x]) => <span key={k} style={{ marginRight: 10 }}>{k} = <span className="mono" data-null={x === null ? 'true' : 'false'}>{valueText(x)}</span></span>)}
                    <span className="hint">{v.rows} {v.rows === 1 ? 'row' : 'rows'}</span>
                  </div>
                ))}
                {r.truncated && <div className="hint">and more</div>}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function Definition(p: AccessViewProps & { definition: AccessDefinition }) {
  const d = p.definition
  const open = p.opened === d.reference
  return (
    <section className="card" style={{ margin: '12px 0' }} data-testid={`access-definition-${d.name}`} data-kind={d.kind}>
      <div className="card-body">
        <div className="row" style={{ justifyContent: 'space-between', gap: 12 }}>
          <div>
            <strong className="mono">{d.reference}</strong> <span className="hint">{d.kind === 'semantic-model' ? 'semantic model' : 'entity model'}{d.version !== null ? `, version ${d.version}` : ''}</span>
            <div className="hint">{d.has_row_rules ? 'row rules' : ''}{d.has_row_rules && d.has_column_rules ? ' and ' : ''}{d.has_column_rules ? 'column rules' : ''}</div>
          </div>
          <button type="button" className="btn" onClick={() => p.onOpen(open ? null : d.reference)} data-testid={`access-open-${d.name}`}>{open ? 'Close' : p.isAdmin ? 'Explain and check' : 'Explain'}</button>
        </div>
        {p.isAdmin && <Warnings warnings={d.warnings ?? []} testId={`access-warnings-${d.name}`} />}
        {p.isAdmin && d.row && <RowRules definition={d} />}
        {p.isAdmin && d.columns && <ColumnRules definition={d} />}
        {p.isAdmin && <Tables definition={d} />}
        {open && <ExplainPanel {...p} definition={d} />}
        {open && p.isAdmin && <CoveragePanel {...p} definition={d} />}
      </div>
    </section>
  )
}

function MaskKey(p: AccessViewProps) {
  const exists = p.data?.mask_key?.exists
  if (!p.isAdmin || exists === undefined) return null
  return (
    <section className="card" style={{ margin: '18px 0' }} data-testid="access-mask-key" data-exists={exists ? 'true' : 'false'}>
      <div className="card-body">
        <h2 className="card-title">The key for hash masks</h2>
        <p data-testid="access-mask-key-status">{exists ? 'The group has its key for hash masks. Its value is never shown.' : 'The group has no key for hash masks yet; it is made the first time a hash mask is answered.'}</p>
        <p className="hint">{rotateWarning}</p>
        <button type="button" className="btn" disabled={p.rotateBusy} onClick={p.onRotate} data-testid="access-mask-key-rotate">{p.confirmingRotate ? 'Click again to rotate the key' : 'Rotate the key'}</button>
        {p.rotated && <div className="banner" style={{ marginTop: 8 }} data-testid="access-mask-key-rotated">{p.rotated}</div>}
      </div>
    </section>
  )
}

/** The access rules of the models and entity models this Gateway serves: the rules, their tables and freshness, the warnings, why a person can or cannot see something, the coverage, and the key of the hash mask. */
export function AccessView(p: AccessViewProps) {
  const definitions = p.data?.definitions ?? []
  return (
    <div className="pane" data-testid="gateway-access">
      <div className="page-head">
        <h1 className="page-title">Access rules</h1>
        <span className="page-note">Rules are data in a rules table that a published Build model refreshes; nobody is exempt, administrators included.</span>
      </div>
      <ErrorBanner error={p.error} />
      {p.data && definitions.length === 0 && <p className="hint" data-testid="access-none">No model or entity model this Gateway serves has access rules. They are written in the <span className="mono">access:</span> section of a semantic model or a Store model.</p>}
      {definitions.map((d) => <Definition key={d.reference} {...p} definition={d} />)}
      <MaskKey {...p} />
    </div>
  )
}

/** The route: the rules, an explain form (a viewer asks about themselves), the coverage check and the key's rotation (a second click). */
export function AccessPage() {
  const isAdmin = useIsAdmin()
  const me = useAuthStatus().data?.displayName ?? ''
  const queryClient = useQueryClient()
  const [opened, setOpened] = useState<string | null>(null)
  const [form, setForm] = useState<ExplainForm>(() => emptyForm())
  const [confirmingRotate, setConfirmingRotate] = useState(false)
  const [rotated, setRotated] = useState<string | null>(null)
  const rules = useQuery({ queryKey: ['gateway', 'access'], queryFn: gatewayApi.access.rules, refetchInterval: 15000, staleTime: 0 })
  const model = opened ?? ''
  const explain = useMutation({ mutationFn: () => gatewayApi.access.explain(explainQuery(model, isAdmin ? form : { ...form, subject: me, kind: 'person' })) })
  const coverage = useMutation({ mutationFn: () => gatewayApi.access.coverage(model, form.dataset || undefined) })
  const rotate = useMutation({
    mutationFn: gatewayApi.access.rotateMaskKey,
    onSuccess: (r) => { setConfirmingRotate(false); setRotated(`The key was replaced. ${r.cleared} cache ${r.cleared === 1 ? 'entry was' : 'entries were'} cleared on this instance.`); return queryClient.invalidateQueries({ queryKey: ['gateway', 'access'] }) },
    onError: () => setConfirmingRotate(false),
  })
  const onRotate = () => {
    if (!confirmingRotate) { setConfirmingRotate(true); setRotated(null); return }
    rotate.mutate()
  }
  const onOpen = (reference: string | null) => { setOpened(reference); setForm(emptyForm(isAdmin ? '' : me)); explain.reset(); coverage.reset() }
  return (
    <GatewayShell crumbs={[{ label: 'Access rules', heading: true }]}>
      <AccessView data={rules.data} error={rules.error ?? rotate.error} isAdmin={isAdmin} me={me} opened={opened} form={isAdmin ? form : { ...form, subject: me }} explanation={explain.data} explainError={explain.error}
        explaining={explain.isPending} coverage={coverage.data} coverageError={coverage.error} checking={coverage.isPending} confirmingRotate={confirmingRotate} rotated={rotated} rotateBusy={rotate.isPending}
        onOpen={onOpen} onForm={setForm} onExplain={() => explain.mutate()} onCoverage={() => coverage.mutate()} onRotate={onRotate} />
    </GatewayShell>
  )
}
