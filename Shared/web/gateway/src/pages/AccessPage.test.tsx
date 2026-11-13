import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { columnRuleText, coverageText, durationText, explainQuery, explainReady, isReadsOnly, ruleText, stateLabel, tableAgeText, uncoveredTotal } from '../access'
import type { AccessCoverage, AccessDefinition, AccessExplanation, AccessRulesAnswer, ExplainForm } from '../accessTypes'
import { AccessView, type AccessViewProps } from './AccessPage'

const semantic: AccessDefinition = {
  reference: 'semantic-model/orders', name: 'orders', kind: 'semantic-model', version: 3, has_row_rules: true, has_column_rules: true, max_age_seconds: 3600,
  row: [{ dataset: 'fact_sales', match: 'all', groups: [{ id: 'visible', match: 'all', rules: [
    { id: 'statuses', effect: 'include', table: 'status_access', subject: 'who', subject_kind: 'group', columns: [{ dataset_column: 'status', rules_column: 'status_code' }], max_age_seconds: null },
    { id: 'no_blocked', effect: 'exclude', table: 'blocked_status', subject: 'who', subject_kind: 'group', columns: [{ dataset_column: 'status', rules_column: 'status_code' }], max_age_seconds: 1800 },
  ] }] }],
  columns: [{ subject: 'support', subject_kind: 'group', dataset: 'customers', column: 'email', treatment: 'mask', mask: 'hash' }],
  tables: [
    { table: 'status_access', key: 'w/status_access', state: 'fresh', age_seconds: 600, max_age_seconds: 3600, counted_from: '2026-11-12T09:50:00Z', last_changed: '2026-11-12T09:50:00Z' },
    { table: 'blocked_status', key: 'w/blocked_status', state: 'expired', age_seconds: 7200, max_age_seconds: 1800, counted_from: '2026-11-12T08:00:00Z', last_changed: null },
  ],
  warnings: ['The rules table \'status_access\' is not known to be built by a published Build model.'],
}
const entity: AccessDefinition = {
  reference: 'entity-model/sales', name: 'sales', kind: 'entity-model', version: 1, has_row_rules: true, has_column_rules: false, max_age_seconds: 3600, limits_reads_only: true,
  row: [{ dataset: 'territory', match: 'all', groups: [{ id: 'by_region', match: 'all', rules: [{ id: 'region', effect: 'include', table: 'rules.region_access', subject: 'who', subject_kind: 'group', columns: [{ dataset_column: 'region', rules_column: 'region_code' }], max_age_seconds: null }] }] }],
  columns: [], tables: [],
  warnings: ['Row rules limit reads only, not writes: a person may still change a row they cannot read through the Store\'s own roles and approvals.'],
}

const explanation: AccessExplanation = {
  definition: 'semantic-model/orders', subject: 'Ann', subject_kind: 'person', names: ['ann@example.com', 'Ann', 'east'], fingerprint: 'abc',
  datasets: [{
    dataset: 'fact_sales', match: 'all', refusal: null, summary: 'They read the rows of \'fact_sales\' that the rule rows select: \'statuses\' (2 rows of \'status_access\'); the rows that \'no_blocked\' match are left out.',
    verdict: { included: false, decided_by: ['visible/no_blocked'], text: 'The row is not included: rule \'visible/no_blocked\' removes it.', row: { status: 'Cancelled' } },
    missing_columns: [],
    groups: [{ id: 'visible', match: 'all', included: false, rules: [
      { id: 'statuses', effect: 'include', table: 'status_access', subject: 'who', subject_kind: 'group', columns: [{ dataset_column: 'status', rules_column: 'status_code' }], names: ['east'], rows: [{ subject: 'east', values: { status_code: 'Completed' } }, { subject: 'east', values: { status_code: null } }], rows_truncated: false, holds: true, read_problem: null },
      { id: 'no_blocked', effect: 'exclude', table: 'blocked_status', subject: 'who', subject_kind: 'group', columns: [{ dataset_column: 'status', rules_column: 'status_code' }], names: ['east'], rows: [{ subject: 'east', values: { status_code: 'Cancelled' } }], rows_truncated: false, holds: true, read_problem: null },
    ] }],
  }],
  columns: [{ dataset: 'customers', column: 'email', treatment: 'mask:hash', rules: [{ subject: 'support', subject_kind: 'group', treatment: 'mask', mask: 'hash' }] }],
  tables: semantic.tables!, warnings: [],
}

const coverage: AccessCoverage = {
  definition: 'semantic-model/orders',
  rules: [
    { dataset: 'fact_sales', group: 'visible', rule: 'statuses', effect: 'include', table: 'status_access', columns: [{ dataset_column: 'status', rules_column: 'status_code' }], uncovered: 2, values: [{ values: { status: 'Pending' }, rows: 4 }, { values: { status: null }, rows: 1 }], truncated: false, problem: null },
    { dataset: 'fact_sales', group: 'visible', rule: 'no_blocked', effect: 'exclude', table: 'blocked_status', columns: [{ dataset_column: 'status', rules_column: 'status_code' }], uncovered: 0, values: [], truncated: false, problem: null },
  ],
}

const form: ExplainForm = { subject: 'Ann', kind: 'person', dataset: 'fact_sales', row: [{ column: 'status', value: 'Cancelled' }] }

const props = (over: Partial<AccessViewProps> = {}): AccessViewProps => ({
  data: { definitions: [semantic, entity], mask_key: { exists: true } } satisfies AccessRulesAnswer, error: null, isAdmin: true, me: 'Ada', opened: null, form, explanation: undefined, explainError: null, explaining: false,
  coverage: undefined, coverageError: null, checking: false, confirmingRotate: false, rotated: null, rotateBusy: false,
  onOpen: () => {}, onForm: () => {}, onExplain: () => {}, onCoverage: () => {}, onRotate: () => {}, ...over,
})

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const html = (over?: Partial<AccessViewProps>) => renderToStaticMarkup(<MemoryRouter>{AccessView(props(over))}</MemoryRouter>)

describe('the access words', () => {
  it('say what a rule does, a column rule, a state and an age', () => {
    expect(ruleText(semantic.row![0].groups[0].rules[0])).toBe('Include the rows whose status = status_code appear in status_access for the group named in who')
    expect(ruleText(semantic.row![0].groups[0].rules[1]).startsWith('Remove the rows whose')).toBe(true)
    expect(columnRuleText(semantic.columns![0])).toBe('Group support: mask (hash)')
    expect(stateLabel('stale')).toContain('last good rules are served')
    expect(stateLabel('expired')).toContain('refused')
    expect(durationText(90)).toBe('90 s')
    expect(durationText(3600)).toBe('60 min')
    expect(durationText(7200)).toBe('2 h')
    expect(tableAgeText(semantic.tables![0])).toBe('10 min old, at most 60 min; last refreshed 2026-11-12 09:50 UTC')
    expect(tableAgeText(semantic.tables![1])).toContain('no refresh reported yet, counted from the day it was first served')
  })

  it('build the explain query from the form: only filled rows, the dataset when chosen, and refuse a row without its dataset', () => {
    expect(explainQuery('semantic-model/orders', form)).toEqual({ subject: 'Ann', kind: 'person', model: 'semantic-model/orders', dataset: 'fact_sales', row: ['status=Cancelled'] })
    expect(explainQuery('m', { ...form, dataset: '', row: [{ column: '', value: '' }] })).toEqual({ subject: 'Ann', kind: 'person', model: 'm' })
    expect(explainReady(form)).toBe(true)
    expect(explainReady({ ...form, dataset: '' })).toBe(false)
    expect(explainReady({ ...form, subject: ' ' })).toBe(false)
    expect(explainReady({ ...form, dataset: '', row: [{ column: '', value: '' }] })).toBe(true)
  })

  it('say how many values a rule leaves uncovered, a problem, and count only the include rules', () => {
    expect(coverageText(coverage.rules[0])).toContain('2 combinations of values have no row in status_access: nobody this rule would include reads those rows.')
    expect(coverageText(coverage.rules[1])).toBe('Every value is covered by a row of blocked_status.')
    expect(coverageText({ ...coverage.rules[0], problem: 'no such table' })).toBe('Not checked: no such table')
    expect(uncoveredTotal(coverage.rules)).toBe(2)
    expect(isReadsOnly(entity.warnings![0])).toBe(true)
    expect(isReadsOnly(semantic.warnings![0])).toBe(false)
  })
})

describe('the rules screen', () => {
  it('shows each model with its rules, column rules, the state of every rules table and the warnings', () => {
    const page = html()
    expect(page).toContain('data-testid="access-definition-orders"')
    expect(page).toContain('Include the rows whose status = status_code appear in status_access')
    expect(page).toContain('Group support: mask (hash)')
    expect(page).toContain('hide, then null, then mask')
    expect(page).toContain('data-testid="access-table-state-orders-blocked_status" data-state="expired"')
    expect(page).toContain('data-testid="access-table-state-orders-status_access" data-state="fresh"')
    expect(page).toContain('is not known to be built by a published Build model')
  })

  it('shows the warning that row rules limit reads only on the rules screen of an entity model, in a banner (the owner requires it to be visible)', () => {
    const page = html()
    expect(page).toContain('data-testid="access-reads-only"')
    expect(page).toContain('Row rules limit reads only, not writes')
    // and only the entity model has it
    expect(page.match(/data-testid="access-reads-only"/g)).toHaveLength(1)
  })

  it('shows the explanation: who the rules see, the rule that decided, the rows that matched (NULL as NULL) and the state of each table', () => {
    const page = html({ opened: 'semantic-model/orders', explanation })
    expect(page).toContain('data-testid="access-explain-orders"')
    expect(page).toContain('The rules see Ann (person) as: ann@example.com, Ann, east')
    expect(page).toContain('data-testid="access-verdict-fact_sales" data-included="false" data-decided-by="visible/no_blocked"')
    expect(page).toContain('rule &#x27;visible/no_blocked&#x27; removes it')
    expect(page).toContain('status_code = Completed')
    expect(page).toContain('status_code = NULL')
    expect(page).toContain('data-testid="access-explained-table-blocked_status" data-state="expired"')
    expect(page).toContain('data-testid="access-explained-column-customers-email" data-treatment="mask:hash"')
  })

  it('shows the coverage: the values no rule row covers, a NULL as NULL, and the count', () => {
    const page = html({ opened: 'semantic-model/orders', coverage })
    expect(page).toContain('data-testid="access-coverage-result" data-uncovered="2"')
    expect(page).toContain('data-testid="access-coverage-rule-fact_sales-statuses" data-uncovered="2"')
    expect(page).toContain('status = <span class="mono" data-null="false">Pending</span>')
    expect(page).toContain('data-null="true">NULL</span>')
    expect(page).toContain('4 rows')
  })

  it('offers the form and the run button only for the opened definition, and disables Explain until the form is ready', () => {
    expect(html()).not.toContain('data-testid="access-explain-run"')
    const open = props({ opened: 'entity-model/sales' })
    expect(find(AccessView(open), 'access-explain-run')?.props.disabled).toBe(false)
    expect(find(AccessView({ ...open, form: { ...form, dataset: '' } }), 'access-explain-run')?.props.disabled).toBe(true)
    expect(find(AccessView({ ...open, explaining: true }), 'access-explain-run')?.props.disabled).toBe(true)
  })

  it('shows the key for hash masks to an administrator with a rotate that waits for a second click and says what rotation does', () => {
    const page = html()
    expect(page).toContain('data-testid="access-mask-key" data-exists="true"')
    expect(page).toContain('The group has its key for hash masks. Its value is never shown.')
    expect(page).toContain('changes every hash mask')
    expect(find(AccessView(props()), 'access-mask-key-rotate')).toBeDefined()
    expect(html({ confirmingRotate: true })).toContain('Click again to rotate the key')
    expect(html({ rotated: 'The key was replaced. 3 cache entries were cleared on this instance.' })).toContain('data-testid="access-mask-key-rotated"')
    expect(html({ data: { definitions: [semantic], mask_key: { exists: false } } })).toContain('no key for hash masks yet')
  })

  it('shows a viewer the names and the form to ask about themselves, and neither the rules nor the key nor the coverage', () => {
    const viewer = props({ isAdmin: false, data: { definitions: [{ reference: 'semantic-model/orders', name: 'orders', kind: 'semantic-model', version: 3, has_row_rules: true, has_column_rules: true }] }, opened: 'semantic-model/orders', form: { ...form, subject: 'Ada' } })
    const page = renderToStaticMarkup(<MemoryRouter>{AccessView(viewer)}</MemoryRouter>)
    expect(page).toContain('data-testid="access-definition-orders"')
    expect(page).toContain('You can ask about yourself.')
    expect(page).not.toContain('access-row-rules-orders')
    expect(page).not.toContain('access-mask-key')
    expect(page).not.toContain('access-coverage-orders')
    expect(find(AccessView(viewer), 'access-subject')?.props.readOnly).toBe(true)
    expect(find(AccessView(viewer), 'access-kind')?.props.disabled).toBe(true)
  })

  it('says when nothing has rules, and shows a refusal of the Gateway', () => {
    expect(html({ data: { definitions: [] } })).toContain('data-testid="access-none"')
    expect(html({ error: new Error('This call needs the admin role.') })).toContain('This call needs the admin role.')
  })
})
