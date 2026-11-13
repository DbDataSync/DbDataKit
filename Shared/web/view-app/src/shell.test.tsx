// @vitest-environment happy-dom
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { connectHost } from './data'

// The shell page's script is C# (`AppShell.ScriptTemplate`, the page that frames every app): this test runs the very text of it in a DOM, with each frame a fake window that records what it is sent, so the dialog and the panel
// (plan 32 DBR36) are exercised without a Gateway. What the browser adds (a real sandbox, a real token round trip) is the story `apps-shell-surface.spec.ts`.
const source = readFileSync(resolve(process.cwd(), '../../src/DbDataKit.Apps.Host/AppShell.cs'), 'utf8')   // the package's folder is the working directory
const start = source.indexOf('private const string ScriptTemplate = """') + 'private const string ScriptTemplate = """'.length
const script = source.slice(start, source.indexOf('""";', start)).replace('__TOKENS__', '{"light":{},"dark":{}}')

interface Fake { posted: Array<Record<string, unknown>>; postMessage(m: Record<string, unknown>): void }
const fakes = new WeakMap<object, Fake>()
const fakeOf = (frame: Element): Fake => {
  let fake = fakes.get(frame)
  if (!fake) { const f: Fake = { posted: [], postMessage(m) { f.posted.push(m) } }; fakes.set(frame, f); fake = f }
  return fake
}

let main: HTMLIFrameElement
const surfaceFrame = () => document.querySelector('[data-testid="shell-surface-frame"]') as HTMLIFrameElement | null
const say = (from: Fake, data: unknown) => window.dispatchEvent(new window.MessageEvent('message', { data, source: from as unknown as Window }))
const open = (over: Record<string, unknown> = {}) => say(fakeOf(main), { dbdata: 'app/1', type: 'surface.open', id: 'a1', as: 'dialog', label: 'New order', route: '/views/orders-form.yaml?customer_id=3&_lock=customer_id', ...over })
const tick = () => new Promise((r) => setTimeout(r, 0))

beforeEach(() => {
  Object.defineProperty(window.HTMLIFrameElement.prototype, 'contentWindow', { configurable: true, get() { return fakeOf(this as Element) } })
  document.body.innerHTML = '<header class="portal">h</header><div class="row"><main><iframe id="app-frame" data-instance="sales" data-theme="{&quot;scheme&quot;:&quot;light&quot;}" sandbox="allow-scripts" src="/apps/sales/_app/"></iframe></main></div>'
  main = document.getElementById('app-frame') as HTMLIFrameElement
  new Function(script)()
})
afterEach(() => { document.body.innerHTML = '' })

describe('the shell page opens a dialog or a panel for a view in an app\'s frame', () => {
  it('opens a dialog as a sandboxed frame of the same instance at the route with `_as`, makes the page behind it inert, and puts focus in', () => {
    open()
    const box = document.querySelector('[data-testid="shell-surface-dialog"]')!
    expect(box).not.toBeNull()
    expect(box.querySelector('[role="dialog"]')?.getAttribute('aria-modal')).toBe('true')
    expect(box.querySelector('[role="dialog"]')?.getAttribute('aria-label')).toBe('New order')
    const frame = surfaceFrame()!
    expect(frame.getAttribute('data-instance')).toBe('sales')
    expect(frame.getAttribute('data-route')).toBe('/views/orders-form.yaml?customer_id=3&_lock=customer_id&_as=dialog')
    expect(frame.getAttribute('sandbox')).toBe('allow-scripts allow-forms allow-popups')
    expect(frame.getAttribute('src')).toBe('/apps/sales/_app/')
    expect(document.querySelector('.row')!.hasAttribute('inert')).toBe(true)   // the page's content, the app's frame in it
    
    expect(document.querySelector('header.portal')!.hasAttribute('inert')).toBe(true)
    expect(document.activeElement).toBe(document.querySelector('[data-testid="shell-surface-close"]'))
  })

  it('answers the surface frame\'s token request (a frame nested in the app\'s own document could get none) and starts it at its route', async () => {
    const calls: string[] = []
    ;(globalThis as { fetch?: unknown }).fetch = async (url: string) => { calls.push(url); return new Response(JSON.stringify({ token: 'tok', expiresIn: 60, audience: 'app:sales' }), { status: 200 }) }
    open()
    const frame = surfaceFrame()!
    frame.dispatchEvent(new window.Event('load'))
    expect(fakeOf(frame).posted.find((m) => m.type === 'navigate')?.path).toBe('/views/orders-form.yaml?customer_id=3&_lock=customer_id&_as=dialog')
    expect(fakeOf(frame).posted.find((m) => m.type === 'theme')).toBeDefined()
    say(fakeOf(frame), { dbdata: 'app/1', type: 'token.request', id: 't1' })
    await tick()
    expect(calls).toEqual(['/apps/sales/_token'])
    expect(fakeOf(frame).posted.find((m) => m.type === 'token')).toMatchObject({ id: 't1', token: 'tok' })
  })

  it('closes on the form\'s views:saved, tells the frame that asked it saved, gives the page back and returns focus to it', () => {
    open()
    const frame = surfaceFrame()!
    say(fakeOf(main), { type: 'views:saved' })                  // not the surface's window: ignored
    expect(document.querySelector('[data-testid="shell-surface-dialog"]')).not.toBeNull()
    say(fakeOf(frame), { type: 'views:saved' })
    expect(document.querySelector('[data-testid="shell-surface-dialog"]')).toBeNull()
    expect(fakeOf(main).posted.find((m) => m.type === 'surface.closed')).toEqual({ dbdata: 'app/1', type: 'surface.closed', id: 'a1', saved: true })
    expect(document.querySelector('.row')!.hasAttribute('inert')).toBe(false)
    expect(document.querySelector('header.portal')!.hasAttribute('inert')).toBe(false)
    expect(document.activeElement).toBe(main)
  })

  it('closes unsaved on views:close, on Close, on the scrim and on Escape; a close after a save still says saved', () => {
    for (const how of ['message', 'button', 'scrim', 'escape'] as const) {
      fakeOf(main).posted.length = 0
      open()
      if (how === 'message') say(fakeOf(surfaceFrame()!), { type: 'views:close' })
      if (how === 'button') (document.querySelector('[data-testid="shell-surface-close"]') as HTMLElement).click()
      if (how === 'scrim') (document.querySelector('[data-testid="shell-surface-scrim"]') as HTMLElement).click()
      if (how === 'escape') document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
      expect(document.querySelector('[data-testid="shell-surface-dialog"]')).toBeNull()
      expect(fakeOf(main).posted.find((m) => m.type === 'surface.closed')?.saved).toBe(false)
    }
    open()
    say(fakeOf(surfaceFrame()!), { type: 'views:saved' })
    expect(fakeOf(main).posted.at(-1)?.saved).toBe(true)
  })

  it('opens a panel as a side sheet that leaves the page usable, and one surface at a time', () => {
    open({ as: 'panel', id: 'p1' })
    expect(document.querySelector('[data-testid="shell-surface-panel"]')).not.toBeNull()
    expect(document.querySelector('[data-testid="shell-surface-scrim"]')).toBeNull()
    expect(document.querySelector('.row')!.hasAttribute('inert')).toBe(false)
    open({ id: 'p2' })
    expect(fakeOf(main).posted.find((m) => m.type === 'surface.refused')).toEqual({ dbdata: 'app/1', type: 'surface.refused', id: 'p2' })
    expect(document.querySelectorAll('[data-testid="shell-surface-frame"]')).toHaveLength(1)
  })

  it('refuses a route that is not a path on the host, a way that is neither, and says so to the frame that asked', () => {
    for (const bad of [{ route: '//evil.example/x' }, { route: 'https://evil.example/' }, { route: '/a\u0001b' }, { as: 'drawer' }, { route: '/' + 'a'.repeat(600) }]) {
      fakeOf(main).posted.length = 0
      open({ id: 'x', ...bad })
      expect(surfaceFrame()).toBeNull()
      expect(fakeOf(main).posted).toEqual([{ dbdata: 'app/1', type: 'surface.refused', id: 'x' }])
    }
  })
})

describe('the app side of the conversation', () => {
  it('asks the shell for a surface by message and hears when it closes, saved or not, or is refused', () => {
    const listeners: Array<(e: MessageEvent) => void> = []
    const sent: Array<Record<string, unknown>> = []
    const parent = { postMessage: (m: Record<string, unknown>) => sent.push(m) }
    const win = { parent, location: { pathname: '/apps/sales/_app/' }, addEventListener: (_: string, l: (e: MessageEvent) => void) => listeners.push(l) } as unknown as Window
    const host = connectHost(win)
    const results: boolean[] = []
    host.openSurface({ as: 'panel', label: 'Order', address: '/views/o.yaml?id=1' }, (saved) => results.push(saved))
    host.openSurface({ as: 'dialog', label: 'Other', address: '/views/p.yaml' }, (saved) => results.push(saved))
    expect(sent.map(({ type, as, label, route }) => ({ type, as, label, route }))).toEqual([
      { type: 'surface.open', as: 'panel', label: 'Order', route: '/views/o.yaml?id=1' }, { type: 'surface.open', as: 'dialog', label: 'Other', route: '/views/p.yaml' },
    ])
    const from = (data: unknown, source: unknown = parent) => listeners.forEach((l) => l({ data, source } as MessageEvent))
    from({ dbdata: 'app/1', type: 'surface.closed', id: sent[0]!.id, saved: true }, {})   // not the page above: ignored
    expect(results).toEqual([])
    from({ dbdata: 'app/1', type: 'surface.closed', id: sent[0]!.id, saved: true })
    from({ dbdata: 'app/1', type: 'surface.refused', id: sent[1]!.id })
    expect(results).toEqual([true, false])
    from({ dbdata: 'app/1', type: 'surface.closed', id: sent[0]!.id, saved: true })      // told once
    expect(results).toHaveLength(2)
  })
})
