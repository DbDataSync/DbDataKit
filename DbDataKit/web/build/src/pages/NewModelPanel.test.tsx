import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { MODEL_KINDS, NewModelView, createModelFailure, modelNameProblem, queryFileOf, type NewModelViewProps } from './NewModelPanel'

function props(over: Partial<NewModelViewProps> = {}): NewModelViewProps {
  return { name: '', kind: 'view', connections: [], connection: [], busy: false, failure: null, onName: () => {}, onKind: () => {}, onConnection: () => {}, onCreate: () => {}, onCancel: () => {}, ...over }
}

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

describe('New model', () => {
  it('offers the four kinds the tool can start, and says what the chosen one is', () => {
    const html = renderToStaticMarkup(NewModelView(props({ kind: 'incremental_by_time_range' })))
    for (const k of MODEL_KINDS) expect(html).toContain(`value="${k.key}"`)
    expect(MODEL_KINDS.map((k) => k.key)).toEqual(['view', 'full', 'incremental_by_unique_key', 'incremental_by_time_range'])
    expect(html).toContain('event_ts')
  })

  it('needs a name of the form schema_name.object_name before it makes anything', () => {
    const disabled = (name: string, busy = false) => find(NewModelView(props({ name, busy })), 'build-new-model-create')?.props.disabled
    expect(disabled('')).toBe(true)
    expect(disabled('orders')).toBe(true)
    expect(disabled('marts.fct returns')).toBe(true)
    expect(disabled('marts.fct_returns', true)).toBe(true)
    expect(disabled('marts.fct_returns')).toBe(false)
    expect(modelNameProblem('orders')).toContain('schema_name.object_name')
    expect(modelNameProblem('marts.fct_returns')).toBeNull()
    expect(modelNameProblem('')).toBeNull()
  })

  it('lets the connections be chosen only where there is a choice', () => {
    expect(renderToStaticMarkup(NewModelView(props({ connections: ['sqlserver'] })))).not.toContain('build-new-model-connection-')
    const view = NewModelView(props({ connections: ['postgres', 'sqlserver'], connection: ['postgres'] }))
    expect(renderToStaticMarkup(view)).toContain('build-new-model-connection-sqlserver')
    const calls: string[] = []
    const choose = find(NewModelView(props({ connections: ['postgres', 'sqlserver'], onConnection: (c, on) => calls.push(`${c} ${on}`) })), 'build-new-model-connection-sqlserver')!
    ;(choose.props.onChange as (e: unknown) => void)({ target: { checked: true } })
    expect(calls).toEqual(['sqlserver true'])
  })

  it('opens the query file the tool wrote', () => {
    expect(queryFileOf(['models/marts/fct_returns.yml', 'models/marts/fct_returns.sql'])).toBe('models/marts/fct_returns.sql')
    expect(queryFileOf([])).toBeUndefined()
  })

  it('gives the tool’s own sentence when the model exists already, and never a trace', () => {
    const answer = { exit: 2, text: null, document: { ok: false, data: {}, errors: ['`models/marts/fct_returns.yml` already exists: `marts.fct_returns` is a model of this project (or its file is in the way). Nothing was written.'] } }
    expect(createModelFailure(answer)).toContain('already exists')
    expect(createModelFailure({ exit: 70, text: 'The command did not answer.\n   at Foo.Bar()', document: null })).toBe('The command did not answer.')
    expect(createModelFailure({ exit: 0, text: null, document: { ok: true, data: {} } })).toBeNull()
    expect(renderToStaticMarkup(NewModelView(props({ failure: 'Nope.' })))).toContain('build-new-model-failed')
  })
})
