// @vitest-environment happy-dom
import { act } from 'react'
import { createRoot, type Root } from 'react-dom/client'
import { renderToStaticMarkup } from 'react-dom/server'
import { afterEach, describe, expect, it } from 'vitest'
import { ChecksTable, ReplicationChecksPanel } from './ReplicationChecksPanel'
import type { CheckForm } from '../types'

;(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true

const noop = () => {}

describe('the list of checks', () => {
  it('says there are none', () => {
    expect(renderToStaticMarkup(<ChecksTable checks={[]} canEdit onEdit={noop} onRemove={noop} />)).toContain('data-testid="replication-checks-empty"')
  })

  it('has a row for each check with its kind and severity, and edit and remove buttons for an author', () => {
    const html = renderToStaticMarkup(<ChecksTable canEdit onEdit={noop} onRemove={noop} checks={[
      { id: 'orders_fresh', kind: 'freshness', dataset: 'dbo.orders', max_age: '10m', severity: 'block', on_error: 'alert' },
      { id: 'name_present', kind: 'not_null', dataset: 'dbo.orders', column: 'Name', severity: 'warn', on_error: 'block' },
    ]} />)
    expect(html).toContain('data-testid="replication-check-orders_fresh" data-kind="freshness" data-severity="block"')
    expect(html).toContain('dbo.orders: no older than 10m')
    expect(html).toContain('blocks on error')
    expect(html).toContain('data-testid="replication-check-edit-name_present"')
    expect(html).toContain('data-testid="replication-check-remove-name_present"')
    expect(html).not.toContain('disabled')
  })

  it('disables edit and remove for a person who may not change the draft, and says why in the button\'s title', () => {
    const html = renderToStaticMarkup(<ChecksTable canEdit={false} onEdit={noop} onRemove={noop} checks={[{ id: 'a', kind: 'unique', dataset: 'd', column: 'k', severity: 'warn' }]} />)
    expect(html.match(/disabled/g)).toHaveLength(2)
    expect(html).toContain('Editing a check needs the author role on this draft.')
  })
})

describe('the panel', () => {
  let root: Root | null = null
  let host: HTMLDivElement
  afterEach(() => { if (root) act(() => root!.unmount()); root = null; host?.remove() })

  const render = async (checks: CheckForm[], onChange: (next: CheckForm[]) => void) => {
    host = document.createElement('div')
    document.body.appendChild(host)
    root = createRoot(host)
    await act(async () => { root!.render(<ReplicationChecksPanel checks={checks} onChange={onChange} />) })
  }
  const q = (id: string) => host.querySelector(`[data-testid="${id}"]`) as HTMLInputElement | HTMLSelectElement | HTMLButtonElement
  const type = async (id: string, value: string) => {
    const el = q(id) as HTMLInputElement
    await act(async () => {
      Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value')!.set!.call(el, value)
      el.dispatchEvent(new Event(el.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }))
    })
  }

  it('adds a freshness check to the list as the file writes it, taking the dataset of the checks already there', async () => {
    let written: CheckForm[] | null = null
    await render([{ id: 'name_present', kind: 'not_null', dataset: 'dbo.orders', column: 'Name', severity: 'warn', on_error: 'alert' }], (next) => { written = next })
    await act(async () => { (q('replication-check-add') as HTMLButtonElement).click() })
    expect((q('replication-check-dataset-input') as HTMLInputElement).value).toBe('dbo.orders')
    expect((q('replication-check-apply') as HTMLButtonElement).disabled).toBe(true)
    expect(q('replication-check-blocked').textContent).toBe('A check needs an id.')
    await type('replication-check-id-input', 'orders_fresh')
    await type('replication-check-maxAge-input', '6h')
    await act(async () => { (q('replication-check-apply') as HTMLButtonElement).click() })
    expect(written).toEqual([
      { id: 'name_present', kind: 'not_null', dataset: 'dbo.orders', column: 'Name', severity: 'warn', on_error: 'alert' },
      { id: 'orders_fresh', kind: 'freshness', dataset: 'dbo.orders', severity: 'warn', on_error: 'alert', description: null, max_age: '6h' },
    ])
    expect(q('replication-check-editor')).toBeNull()
  })

  it('removes a check from the list', async () => {
    let written: CheckForm[] | null = null
    await render([{ id: 'a', kind: 'unique', dataset: 'd', column: 'k' }, { id: 'b', kind: 'not_null', dataset: 'd', column: 'k' }], (next) => { written = next })
    await act(async () => { (q('replication-check-remove-a') as HTMLButtonElement).click() })
    expect(written).toEqual([{ id: 'b', kind: 'not_null', dataset: 'd', column: 'k' }])
  })
})
