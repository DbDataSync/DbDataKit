// @vitest-environment happy-dom
import { act, createElement } from 'react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { defineWidget, definePage, embeddedInit, frameRequest, startViewFrame, useReady, useView } from './index'
import type { HostPort, ToHost, ViewProps } from './index'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const props = (): ViewProps => ({ data: { rows: { rows: [{ n: 1 }, { n: 2 }, { n: 3 }] } }, params: {}, theme: { scheme: 'light', tokens: {} }, size: { width: 600, height: 'auto' }, mode: 'print', links: {} })

describe('the frame address', () => {
  it('names the view and the mode in the fragment; anything else is not a frame', () => {
    expect(frameRequest('#view=OrdersGrid&mode=print')).toEqual({ view: 'OrdersGrid', mode: 'print' })
    expect(frameRequest('#view=OrdersGrid')).toEqual({ view: 'OrdersGrid', mode: 'screen' })
    expect(frameRequest('#view=OrdersGrid&mode=other')).toEqual({ view: 'OrdersGrid', mode: 'screen' })
    for (const bad of ['', '#', '#mode=print', '#view=1bad', '#view=a/b', '#view=' + 'x'.repeat(65)]) expect(frameRequest(bad)).toBeNull()
  })
})

describe('a page printed as the top-level document', () => {
  let root: HTMLElement
  beforeEach(() => { root = document.createElement('div'); document.body.appendChild(root) })
  afterEach(() => { root.remove(); document.getElementById('dbdatakit-view-init')?.remove(); delete (window as { __reportReady?: boolean }).__reportReady })

  it('reads the embedded view and refuses a block that is not one', () => {
    expect(embeddedInit()).toBeNull()
    const block = Object.assign(document.createElement('script'), { id: 'dbdatakit-view-init', type: 'application/json', textContent: '{not json' })
    document.body.appendChild(block)
    expect(embeddedInit()).toBeNull()
    block.textContent = JSON.stringify({ name: 'Regional', props: props() })
    expect(embeddedInit()?.name).toBe('Regional')
    block.textContent = JSON.stringify({ name: 3, props: props() })
    expect(embeddedInit()).toBeNull()
  })

  it('mounts at once with the embedded rows and no host; ready() sets the flag the exporter reads and sends the height first', async () => {
    document.body.appendChild(Object.assign(document.createElement('script'), { id: 'dbdatakit-view-init', type: 'application/json', textContent: JSON.stringify({ name: 'EmbeddedPage', props: props() }) }))
    definePage('EmbeddedPage', function EmbeddedPage() {
      const view = useView()
      const { ready } = useReady()
      return createElement('p', { 'data-testid': 'rows', ref: () => { void ready() } }, `${view.data.rows!.rows.length} rows in ${view.mode}`)
    })
    const sent: ToHost[] = []
    const port: HostPort = { send: (m) => sent.push(m), subscribe: () => () => {} }
    let started = false
    await act(async () => { started = startViewFrame({ root, port: undefined }) })
    expect(started).toBe(true)
    expect(root.textContent).toBe('3 rows in print')
    await act(async () => { await Promise.resolve() })
    expect((window as { __reportReady?: boolean }).__reportReady).toBe(true)
    void port; void sent
  })

  it('a widget registered under a name the host did not ask for fails with the names it has', async () => {
    defineWidget('KnownOne', () => null)
    const sent: ToHost[] = []
    const listeners: Array<(m: never) => void> = []
    const port: HostPort = { send: (m) => sent.push(m), subscribe: (l) => { listeners.push(l as never); return () => {} } }
    await act(async () => { startViewFrame({ root, port, request: { view: 'Missing', mode: 'screen' } }) })
    await act(async () => { listeners.forEach((l) => l({ type: 'views:init', name: 'Missing', props: props() } as never)) })
    const error = sent.find((m) => m.type === 'views:error') as Extract<ToHost, { type: 'views:error' }> | undefined
    expect(error?.message).toContain("registers no view named 'Missing'")
    expect(error?.message).toContain('KnownOne')
  })
})
