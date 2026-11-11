import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { parseYaml } from '@dbdatakit/shell'
import type { CheckDiagnostic } from '../api'
import { editorKindOf, liveState, liveWord, markersFor, splitFindings } from '../editors'
import type { LiveCheck } from '../useLiveCheck'
import { FindingsList } from './StructuredPanel'
import { StoreModelEditor, modelSummary, newModelProblem, newWorkspaceModelText } from './StoreModelPanel'

const duplicate: CheckDiagnostic = { severity: 'error', code: 'DDM-204', message: 'Attribute code is declared twice.', file: 'models/orders.yaml', line: 9 }
const elsewhere: CheckDiagnostic = { severity: 'error', code: 'DBD-WS-STORE-TWICE', message: 'Two files declare the model sales.', file: 'models/sales2.yaml', line: null }
const noFile: CheckDiagnostic = { severity: 'warning', code: 'DBD-WS-STORE-NONE', message: 'This draft has no model.', file: null, line: null }
const idle: LiveCheck = { diagnostics: [], checkedText: null, running: false, failed: null }

describe('which editor a file gets', () => {
  it('opens a model of a store project as a model, wherever under models/ it is', () => {
    expect(editorKindOf('store', 'models/orders.yaml')).toBe('store-model')
    expect(editorKindOf('store', 'models/sales/orders.yml')).toBe('store-model')
    expect(editorKindOf('store', 'README.md')).toBeNull()
    expect(editorKindOf('store', 'other/orders.yaml')).toBeNull()
  })
  it('gives nothing to a project of another kind', () => {
    expect(editorKindOf('app', 'models/orders.yaml')).toBeNull()
    expect(editorKindOf('build', 'models/orders.yaml')).toBeNull()
    expect(editorKindOf(undefined, 'models/orders.yaml')).toBeNull()
  })
})

describe('the check beside an editor', () => {
  it('marks the lines of this file and a finding with no file on the first line', () => {
    const marks = markersFor('models/orders.yaml', [duplicate, elsewhere, noFile])
    expect(marks).toEqual([
      { line: 9, column: 1, message: 'DDM-204: Attribute code is declared twice.', severity: 'error' },
      { line: 1, column: 1, message: 'DBD-WS-STORE-NONE: This draft has no model.', severity: 'warning' },
    ])
  })
  it('keeps the findings about other files apart', () => {
    const { here, elsewhere: other } = splitFindings('models/orders.yaml', [duplicate, elsewhere, noFile])
    expect(here).toHaveLength(2)
    expect(other).toEqual([elsewhere])
  })
  it('says how far it has got, and a failed check is not a sound draft', () => {
    expect(liveState({ text: 'a', checkedText: 'a', failed: null, running: false })).toBe('current')
    expect(liveState({ text: 'ab', checkedText: 'a', failed: null, running: false })).toBe('stale')
    expect(liveState({ text: 'ab', checkedText: 'a', failed: null, running: true })).toBe('checking')
    expect(liveState({ text: 'a', checkedText: 'a', failed: 'down', running: false })).toBe('failed')
    expect(liveWord('current', 0, 0)).toBe('The draft is sound.')
    expect(liveWord('current', 0, 2)).toBe('Sound, with 2 warnings.')
    expect(liveWord('current', 1, 1)).toBe('1 problem to fix, 1 warning.')
    expect(liveWord('failed', 0, 0)).toBe('The check did not run.')
  })
  it('lists each finding with its place and a button for the other files', () => {
    const html = renderToStaticMarkup(<FindingsList path="models/orders.yaml" diagnostics={[duplicate, elsewhere]} state="current" onOpen={() => {}} />)
    expect(html).toContain('data-errors="1"')
    expect(html).toContain('line 9')
    expect(html).toContain('DDM-204')
    expect(html).toContain('1 more finding (1 to fix) in other files')
    expect(html).toMatch(/<button[^>]*data-testid="wsp-live-open"[^>]*>models\/sales2\.yaml<\/button>/)
  })
})

describe('a model of the Store in the Workspace', () => {
  it('starts a new model at the current format with the name typed', () => {
    const text = newWorkspaceModelText('sales_territories')
    expect(text).toContain('schemaVersion: 2')
    expect(text).toContain('name: sales_territories')
    expect(parseYaml(text).errors).toEqual([])
  })
  it('refuses a name the Store would refuse, and one already taken in the draft', () => {
    expect(newModelProblem('', [])).toBeNull()
    expect(newModelProblem('Sales', [])).toContain('lower-case')
    expect(newModelProblem('orders', ['models/orders.yaml'])).toContain('already exists')
    expect(newModelProblem('orders', ['models/other.yaml'])).toBeNull()
  })
  it('summarizes the entities and attributes, and says when the text is not a model', () => {
    expect(modelSummary(newWorkspaceModelText('sales'))).toBe('sales: 1 entity, 2 attributes')
    expect(modelSummary('entities: [')).toBe('The text is not a model yet.')
  })
  it('draws the form from the Store schema over the text, with the check beside it', () => {
    const text = newWorkspaceModelText('sales')
    const html = renderToStaticMarkup(<StoreModelEditor path="models/orders.yaml" text={text} onText={() => {}} readOnly={false} live={{ ...idle, diagnostics: [duplicate], checkedText: text }} />)
    expect(html).toContain('data-testid="wsp-form"')
    expect(html).toContain('sales: 1 entity, 2 attributes')
    expect(html).toContain('data-testid="wsp-live-finding"')
    expect(html).toContain('DDM-204')
  })
  it('makes the form read only without the author role', () => {
    const html = renderToStaticMarkup(<StoreModelEditor path="models/sales.yaml" text={newWorkspaceModelText('sales')} onText={() => {}} readOnly live={idle} />)
    expect(html).toMatch(/<fieldset[^>]*disabled/)
  })
  it('shows when the check could not run', () => {
    const html = renderToStaticMarkup(<StoreModelEditor path="models/sales.yaml" text={newWorkspaceModelText('sales')} onText={() => {}} readOnly={false} live={{ ...idle, failed: 'The server is not answering.' }} />)
    expect(html).toContain('wsp-live-failed')
    expect(html).toContain('The server is not answering.')
  })
})
