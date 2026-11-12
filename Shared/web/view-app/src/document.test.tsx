// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { fixtureClient, type ViewsClient } from '@dbdatakit/visuals'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { DocumentScreen } from './document'
import { ViewApp } from './screens'
import type { ViewsDoc } from './spec'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const FILE = 'views/overview.yaml'
const TEXT = `schemaVersion: 1
kind: dashboard
title: Overview
parameters:
  - {name: region, type: string, default: all}
widgets:
  note: {type: text, text: "Region {{params.region}}"}
layouts:
  - {name: wall, type: reflow, default: true, grid: {columns: 12}, tiles: [{widget: note, x: 0, y: 0, w: 12, h: 2}]}
`

class FakeObserver {
  callback: unknown
  constructor(callback: unknown) { this.callback = callback }
  observe() {}
  unobserve() {}
  disconnect() {}
}

let host: HTMLDivElement
let root: Root
beforeEach(() => {
  ;(globalThis as { ResizeObserver?: unknown }).ResizeObserver = FakeObserver
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, get: () => 1200 })
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, get: () => 700 })
  host = document.createElement('div')
  document.body.appendChild(host)
  root = createRoot(host)
})
afterEach(async () => {
  await act(async () => root.unmount())
  host.remove()
})

const appDoc = (files: ViewsDoc['views']): ViewsDoc => ({ format: 'view-spec/1', app: 'sales', instance: 'sales', title: 'Sales', home: FILE, files: Object.keys(files), problems: [], views: files })
const files: ViewsDoc['views'] = { [FILE]: { kind: 'document', document: 'dashboard', title: 'Overview', model: '', params: [], links: [] } }

function client(text = TEXT) {
  const calls: { layout: string; widgets: string[]; parameters: unknown }[] = []
  const views = fixtureClient({ documents: { [`dashboard/${FILE}`]: text } }, { onResolve: (c) => calls.push({ layout: c.layout, widgets: c.widgets, parameters: c.parameters }) })
  return { calls, views }
}

async function show(views: ViewsClient, query: Record<string, string> = {}, route: (p: string) => void = () => {}) {
  await act(async () => root.render(<DocumentScreen file={FILE} kind="dashboard" views={views} query={query} doc={appDoc(files)} route={route} go={() => {}} />))
  await act(async () => { await new Promise((r) => setTimeout(r, 0)) })
}

describe('a dashboard of the app', () => {
  it('is read through the views client by its file and drawn by the shared visuals, its widgets resolved by the client', async () => {
    const { calls, views } = client()
    await show(views)
    expect(host.querySelector('[data-testid="va-board"]')?.getAttribute('data-kind')).toBe('dashboard')
    expect(host.textContent).toContain('Region all')   // the default of the declared parameter
    expect(calls).toEqual([{ layout: 'wall', widgets: ['note'], parameters: { region: 'all' } }])
  })

  it('takes its parameters from the route and ignores a name the document does not declare', async () => {
    const { calls, views } = client()
    await show(views, { region: 'West', secret: 'x' })
    expect(host.textContent).toContain('Region West')
    expect(JSON.stringify(calls[0]?.parameters)).toBe('{"region":"West"}')
  })

  it('says why a document that does not read is not drawn, with its place in the file', async () => {
    const { views } = client('schemaVersion: 1\nkind: dashboard\ntitle: Broken\nwidgets:\n  a: {type: nonsense}\nlayouts: []\n')
    await show(views)
    const problems = host.querySelector('[data-testid="va-document-problems"]')
    expect(problems?.textContent).toContain(`${FILE} does not read`)
    expect(host.querySelector('[data-testid="va-board"]')).toBeNull()
  })

  it('shows the views client\'s refusal (a person with no access, a file the app does not have) as an error', async () => {
    const { views } = client()
    await show({ ...views, open: async () => { throw new Error('This app has no dashboard or report.') } })
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('This app has no dashboard or report.')
  })
})

describe('the app', () => {
  it('lists a document in its menu and draws it for its route; without a views client it says so', async () => {
    const { views } = client()
    const shell = { route: () => {}, onNavigate: () => () => {} }
    const both = appDoc({ ...files, 'views/other.yaml': { kind: 'document', document: 'report', title: 'Other', model: '', params: [], links: [] } })
    await act(async () => root.render(<ViewApp doc={both} client={{} as never} host={shell} views={views} />))
    await act(async () => { await new Promise((r) => setTimeout(r, 0)) })
    expect([...host.querySelectorAll('.va-menu a')].map((a) => a.textContent)).toEqual(['Overview', 'Other'])
    expect(host.querySelector('[data-testid="va-board"]')).not.toBeNull()

    await act(async () => root.render(<ViewApp doc={both} client={{} as never} host={shell} />))
    expect(host.textContent).toContain('gives the app no views client')
  })
})
