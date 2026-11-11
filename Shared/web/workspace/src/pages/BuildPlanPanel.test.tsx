import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { CheckDiagnostic } from '../api'
import { findingsOfModel, modelsOf, planFilesOf, planSummary, planWord } from '../buildPlan'
import { ModelsView, PlanView } from './BuildPlanPanel'

const paths = [
  'dbdatabuild.yml', 'connections/sqlserver.yaml',
  'models/staging/orders.sql', 'models/staging/orders.yml', 'models/staging/_dbdatabuild.yml',
  'models/marts/sales/fct_sales.sql',
  'rendered/sqlserver/loads.yml', 'rendered/sqlserver/refresh/hourly.yml', 'rendered/sqlserver/refresh/daily.yml', 'rendered/postgres/loads.yml',
]
const broken: CheckDiagnostic[] = [
  { severity: 'error', code: 'DDB-101', message: 'bad', file: 'models/staging/orders.yml', line: 3 },
  { severity: 'warning', code: 'DDB-210', message: 'no description', file: 'models/marts/sales/fct_sales.sql', line: null },
  { severity: 'error', code: 'DDB-001', message: 'project', file: null, line: null },
]

describe('the models of a Build project', () => {
  it('pairs a model\'s sql with its yml and leaves out a folder\'s own settings', () => {
    const models = modelsOf(paths)
    expect(models.map((m) => `${m.folder}/${m.name}`)).toEqual(['marts/sales/fct_sales', 'staging/orders'])
    expect(models[1].files).toEqual(['models/staging/orders.sql', 'models/staging/orders.yml'])
  })
  it('counts the findings that name a model\'s files', () => {
    const [sales, orders] = modelsOf(paths)
    expect(findingsOfModel(orders, broken)).toEqual({ errors: 1, warnings: 0 })
    expect(findingsOfModel(sales, broken)).toEqual({ errors: 0, warnings: 1 })
  })
  it('draws each model under its folder with its errors and a button for each file', () => {
    const html = renderToStaticMarkup(<ModelsView models={modelsOf(paths)} diagnostics={broken} onOpen={() => {}} />)
    expect(html.match(/data-testid="wsp-build-model"/g)).toHaveLength(2)
    expect(html).toMatch(/data-name="orders" data-errors="1"/)
    expect(html).toContain('1 to fix')
    expect(html.match(/data-testid="wsp-build-model-file"/g)).toHaveLength(3)
  })
  it('says what a model is when there is none', () => {
    expect(renderToStaticMarkup(<ModelsView models={[]} diagnostics={[]} onOpen={() => {}} />)).toContain('wsp-build-no-models')
  })
})

describe('the compiled plan', () => {
  it('groups the rendered files by connection', () => {
    const plans = planFilesOf(paths)
    expect(plans.map((c) => c.connection)).toEqual(['postgres', 'sqlserver'])
    expect(plans[1].loads).toBe('rendered/sqlserver/loads.yml')
    expect(plans[1].plans.map((p) => p.name)).toEqual(['daily', 'hourly'])
  })
  it('reads how many loads a plan runs, loosely, and says when it is not a plan', () => {
    expect(planWord(planSummary('members:\n- model: a\n- model: b\n'))).toBe('2 loads')
    expect(planSummary('members:\n- model: a\n')?.members).toEqual(['a'])
    expect(planWord(planSummary('members: [x]'))).toBe('1 load')
    expect(planWord(planSummary('title: x'))).toBe('not a plan file')
    expect(planSummary('a: [')).toBeNull()
  })
  it('draws each connection with its plans and their sizes, and says what to do when nothing is compiled', () => {
    const html = renderToStaticMarkup(<PlanView plans={planFilesOf(paths)} texts={{ 'rendered/sqlserver/refresh/hourly.yml': 'members:\n- model: a\n' }} onOpen={() => {}} />)
    expect(html.match(/data-testid="wsp-build-connection"/g)).toHaveLength(2)
    expect(html.match(/data-testid="wsp-build-refresh-plan"/g)).toHaveLength(2)
    expect(html).toContain('1 load')
    expect(renderToStaticMarkup(<PlanView plans={[]} texts={{}} onOpen={() => {}} />)).toContain('wsp-build-no-plan')
  })
})
