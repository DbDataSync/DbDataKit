// @dbdatakit/views-sdk: what the code of a custom view builds on (plan 32 section 9f, DBR30). A custom view is app code in the app's sandboxed document: a `widget` a dashboard or a report places in a box, or a `page`,
// a whole dashboard or report written as code. It registers with `defineWidget` / `definePage`; the host (the shell and the exporter) sends it its rows, parameters, theme and size and receives
// `setParam`, `open`, the ready signal and an error. The code never fetches data on the paths that print or email: the host runs the view's declared queries as the viewer and hands the rows in.
//
// This file imports nothing but React and react-dom (the frame's own root, `startViewFrame`): an app's policy forbids what compiles code at load (see `docs/ci-lessons.md`, "Web code that runs under an app's policy"), so it does not import `@dbdatakit/visuals`
// (its validators do); the few pure rules it needs (the route's query string) are written here and have a test against the shared goldens.
import { Component, createContext, createElement, useContext, useEffect, useMemo, useState } from 'react'
import type { ComponentType, ReactNode } from 'react'
import { createRoot } from 'react-dom/client'

export type ViewMode = 'screen' | 'print' | 'image'
export type Scalar = string | number | boolean
export type Row = Record<string, unknown>

/** The rows of each query the view declared in the manifest, by the query's name. */
export type ViewData = Record<string, { columns?: string[]; rows: Row[]; error?: { code: string; message: string } }>

/** A link the document declared and the widget may open, as the host resolved it. `route` is the target's address inside the app; `values` are the link's, still to be read against a row. */
export interface HostLinkInfo {
  route: string | null
  as: 'navigate' | 'dialog' | 'panel'
  values: Record<string, { kind: 'literal' | 'field' | 'param' | 'ask'; text: string; value?: Scalar }>
  /** What the exporter draws: `hidden`, `normal` or `link`, or absent for the element's meaning to decide. */
  print?: 'hidden' | 'normal' | 'link'
  label?: string
  /** The link is an action (a `change` or a `task`): fire it with `useRequest`. */
  action?: boolean
}

/** What a view is given. `size.height` is `'auto'` for a page (it grows and paginates). */
export interface ViewProps {
  data: ViewData
  params: Record<string, Scalar | Scalar[] | null>
  theme: { scheme: 'light' | 'dark'; tokens: Record<string, string> }
  size: { width: number; height: number | 'auto' }
  mode: ViewMode
  links: Record<string, HostLinkInfo>
  /** What is staged in the shell's pending set (plan 32 DBR53b), read-only: show an edited value from it. The set is the shell's, not the widget's. */
  pending?: PendingSummary[]
}

/** One staged change as a widget may read it. */
export interface PendingSummary { link: string; entity: string; operation: 'insert' | 'edit' | 'delete'; key: string; writes: Record<string, string> }

export type ViewComponent = ComponentType<ViewProps>

// ---- registration ----

const registry = new Map<string, { kind: 'widget' | 'page'; component: ViewComponent }>()

/** Registers a widget under the name the manifest's `export` gives it. A name registered twice is refused: two components could not both be the one the document names. */
export function defineWidget(name: string, component: ViewComponent): ViewComponent {
  return define('widget', name, component)
}

/** Registers a page (a whole dashboard or report as code) under the manifest's `export`. */
export function definePage(name: string, component: ViewComponent): ViewComponent {
  return define('page', name, component)
}

function define(kind: 'widget' | 'page', name: string, component: ViewComponent): ViewComponent {
  if (!/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(name)) throw new Error(`'${name}' is not a name a view can register: letters, digits and '_', starting with a letter.`)
  if (registry.has(name)) throw new Error(`The view '${name}' is registered twice.`)
  registry.set(name, { kind, component })
  return component
}

export const registered = (name: string) => registry.get(name)
export const registeredNames = () => [...registry.keys()].sort()

// ---- the conversation with the host ----

/** What the code sends the host. Only these: a widget cannot choose a target or call the shell, it names a link the document declared. */
export type ToHost =
  | { type: 'views:hello' }
  | { type: 'views:ready' }
  | { type: 'views:error'; message: string }
  | { type: 'views:setParam'; name: string; value: Scalar | Scalar[] | null }
  | { type: 'views:open'; link: string; row: Row }
  | { type: 'views:stage'; link: string; row: Row; answers: Record<string, string> }
  | { type: 'views:submit' }
  | { type: 'views:discard' }
  | { type: 'views:size'; height: number }

/** What the host sends the code: the props, again on every change, and that a dialog or panel it opened has closed (`saved` when a form saved). */
export type FromHost =
  | { type: 'views:init'; name: string; props: ViewProps }
  | { type: 'views:props'; props: ViewProps }
  | { type: 'views:closed'; link: string; saved: boolean }

export interface HostPort {
  send(message: ToHost): void
  subscribe(listener: (message: FromHost) => void): () => void
}

/** The port to the document that frames this one: messages are accepted only from it and only of the shapes above. */
export function windowPort(self: Window = window, host: Window = window.parent): HostPort {
  return {
    send: (message) => host.postMessage(message, '*'),
    subscribe: (listener) => {
      const on = (e: MessageEvent) => {
        if (e.source !== host) return
        const m = e.data as { type?: unknown }
        if (m && (m.type === 'views:init' || m.type === 'views:props' || m.type === 'views:closed')) listener(e.data as FromHost)
      }
      self.addEventListener('message', on)
      return () => self.removeEventListener('message', on)
    },
  }
}

interface HostState { port: HostPort; props: ViewProps; mode: ViewMode; closed: { link: string; saved: boolean } | null }
const HostContext = createContext<HostState | null>(null)

function host(): HostState {
  const state = useContext(HostContext)
  if (!state) throw new Error('This is used outside a view: the host mounts a registered view, and the hooks of @dbdatakit/views-sdk work inside it.')
  return state
}

/** The code's own view of the host, for a test: the props it was given, and a recorder of what it sent. */
export function ViewHost({ port, props, children }: { port: HostPort; props: ViewProps; children: ReactNode }) {
  const [current, setCurrent] = useState(props)
  const [closed, setClosed] = useState<HostState['closed']>(null)
  useEffect(() => port.subscribe((m) => { if (m.type === 'views:closed') setClosed({ link: m.link, saved: m.saved }); else setCurrent(m.props) }), [port])
  const value = useMemo<HostState>(() => ({ port, props: current, mode: current.mode, closed }), [port, current, closed])
  return createElement(HostContext.Provider, { value }, children)
}

/**
 * The page's ready signal, which the exporter waits for. Call `ready()` when the view is drawn (it also waits for `document.fonts.ready`, so page breaks are the ones the fonts make); `fail(message)` says the view cannot
 * be drawn, and the export is refused with the words. Both set what the exporter reads (`window.__reportReady`, `window.__reportError`) and tell the host.
 */
export function useReady(): { ready: () => Promise<void>; fail: (message: string) => void } {
  const { port } = host()
  return useMemo(() => ({
    ready: async () => {
      try { await document.fonts?.ready } catch { /* a document without fonts is ready */ }
      ;(window as unknown as { __reportReady?: boolean }).__reportReady = true
      // the final height goes before the ready: the host checks a widget against a page with it
      port.send({ type: 'views:size', height: contentHeight() })
      port.send({ type: 'views:ready' })
    },
    fail: (message: string) => {
      ;(window as unknown as { __reportError?: string }).__reportError = message
      port.send({ type: 'views:error', message })
    },
  }), [port])
}

/** The view's props now: the rows of its declared queries, its parameters, theme, size and mode. */
export function useView(): ViewProps { return host().props }

/** A cross-filter: sets a parameter of the document exactly as a control would (a binding, not a computation). Does nothing in print and image mode, where nothing is interactive. */
export function useSetParam(): (name: string, value: Scalar | Scalar[] | null) => void {
  const { port, mode } = host()
  return (name, value) => { if (mode === 'screen') port.send({ type: 'views:setParam', name, value }) }
}

/** `open(link, row)`: fires the document's link of that name for a row. The code cannot choose a target: the host resolves it, and does nothing in print and image mode. */
export function useOpen(): (link: string, row: Row) => void {
  const { port, mode, props } = host()
  return (link, row) => { if (mode === 'screen' && link in props.links) port.send({ type: 'views:open', link, row }) }
}

/**
 * `request(link, row)`: fires an **action** of the document (a `change` or a `task`, plan 32 DBR53) for a row. The shell shows its own confirmation popup in the **declaration's** words and sends nothing until the person confirms:
 * the code can name only a declared link and give the row, never the popup's text, the values written or the target. Does nothing in print and image mode, or for a link that is not an action.
 */
export function useRequest(): (link: string, row: Row) => void {
  const { port, mode, props } = host()
  return (link, row) => { if (mode === 'screen' && props.links[link]?.action === true) port.send({ type: 'views:open', link, row }) }
}

/**
 * `stage(link, row, answers?)`: stages a change of an **action** link in the shell's pending set (plan 32 DBR53b) instead of asking: thirty edits are thirty calls of this and one review. Nothing is sent; `answers` are the
 * values of the link's `ask` fields. Does nothing in print and image mode, or for a link that is not an action.
 */
export function useStage(): (link: string, row: Row, answers?: Record<string, string>) => void {
  const { port, mode, props } = host()
  return (link, row, answers = {}) => { if (mode === 'screen' && props.links[link]?.action === true) port.send({ type: 'views:stage', link, row, answers }) }
}

/** What the shell has staged, read-only (empty when nothing is). The set belongs to the shell: the code cannot change or confirm it. */
export function usePending(): PendingSummary[] { return host().props.pending ?? [] }

/** `submit()` **opens the shell's review** of the pending set; it cannot confirm: only the person's click in the shell's popup sends the set. `discard()` empties the set. */
export function useBatch(): { submit(): void; discard(): void } {
  const { port, mode } = host()
  return { submit: () => { if (mode === 'screen') port.send({ type: 'views:submit' }) }, discard: () => { if (mode === 'screen') port.send({ type: 'views:discard' }) } }
}

/** The link a dialog or panel last closed for, and whether a form saved: the view may draw again. */
export function useClosed(): { link: string; saved: boolean } | null { return host().closed }

// ---- links ----

/** The values a link gives for a row, as the host reads them: a literal, `$field` of the row (by its full name or the part after the last dot), `$params.name`; a value with nothing is left out. */
export function linkValues(info: HostLinkInfo, row: Row, params: ViewProps['params']): Record<string, Scalar | Scalar[]> {
  const out: Record<string, Scalar | Scalar[]> = {}
  for (const [name, v] of Object.entries(info.values)) {
    const value: unknown = v.kind === 'literal' ? v.value : v.kind === 'param' ? params[v.text] : v.text in row ? row[v.text] : row[v.text.slice(v.text.lastIndexOf('.') + 1)]
    if (value === undefined || value === null) continue
    if (Array.isArray(value)) { if (value.length) out[name] = value as Scalar[]; continue }
    out[name] = typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean' ? value : String(value)
  }
  return out
}

/** The address of a link for a row: the target's route and the values as a sorted query string (the shell keeps the parameters in the address). Null for a link with no address or an unknown name. */
export function linkHref(name: string, row: Row = {}): string | null {
  const { props } = host()
  const info = props.links[name]
  if (!info || info.route === null || info.as !== 'navigate') return null
  const values = linkValues(info, row, props.params)
  const parts: string[] = []
  for (const k of Object.keys(values).sort()) for (const one of Array.isArray(values[k]) ? (values[k] as Scalar[]) : [values[k] as Scalar]) parts.push(`${encodeURIComponent(k)}=${encodeURIComponent(String(one))}`)
  return parts.length ? `${info.route}?${parts.join('&')}` : info.route
}

/**
 * An element that is a link, drawn as the developer chose with `print` (the document's link, else `print` here, else the element's meaning: an element that shows a value is drawn, a pure affordance is not).
 * On screen it is an anchor that fires the link through the host; in print it is drawn with no link behaviour, or as an anchor to the link's address (`link`: a PDF hyperlink; the address is never written as text), or not at all.
 */
export function HostLink({ name, row = {}, print, meaning = 'affordance', children }: { name: string; row?: Row; print?: 'hidden' | 'normal' | 'link'; meaning?: 'value' | 'affordance'; children: ReactNode }) {
  const { props, mode, port } = host()
  const info = props.links[name]
  if (mode === 'screen') {
    return createElement('a', { href: linkHref(name, row) ?? '#', 'data-link': name, onClick: (e: { preventDefault(): void }) => { e.preventDefault(); if (info) port.send({ type: 'views:open', link: name, row }) } }, children)
  }
  let treatment = print ?? info?.print ?? (meaning === 'value' ? 'normal' : 'hidden')
  if (treatment === 'link' && (!info || info.route === null || info.as !== 'navigate')) treatment = meaning === 'value' ? 'normal' : 'hidden'
  if (treatment === 'link' && mode === 'image') treatment = 'normal'
  if (treatment === 'hidden') return null
  if (treatment === 'link') return createElement('a', { href: linkHref(name, row) ?? undefined }, children)
  return createElement('span', null, children)
}

/** A forced page break in a page's code (`break-after: page`); in a widget it does nothing, the widget being one unbreakable block. */
export function PageBreak() {
  return createElement('div', { 'aria-hidden': true, style: { breakAfter: 'page', pageBreakAfter: 'always', height: 0 } })
}

/** The theme as the CSS custom properties the code styles with (`var(--sui-text)`): put them on a container's `style`. */
export function themeStyle(theme: ViewProps['theme']): Record<string, string> {
  return Object.fromEntries(Object.entries(theme.tokens).map(([k, v]) => [k.startsWith('--') ? k : `--${k}`, v]))
}

// ---- the frame: what runs in the sandboxed document the host frames ----

/**
 * What the host's frame address says: the view to mount and the mode, in the fragment (`<app's page>#view=OrdersGrid&mode=print`), which no server sees and which the sandbox keeps. A document opened without it is the app's own screen,
 * not a view frame. `view` is a name a view can register; anything else is not a frame.
 */
export function frameRequest(hash: string = typeof location === 'undefined' ? '' : location.hash): { view: string; mode: ViewMode } | null {
  const params = new URLSearchParams(hash.startsWith('#') ? hash.slice(1) : hash)
  const view = params.get('view')
  if (!view || !/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(view)) return null
  const mode = params.get('mode')
  return { view, mode: mode === 'print' || mode === 'image' ? mode : 'screen' }
}

class FrameBoundary extends Component<{ port: HostPort; children: ReactNode }, { failed: boolean }> {
  state = { failed: false }
  static getDerivedStateFromError() { return { failed: true } }
  componentDidCatch(error: unknown) {
    const message = error instanceof Error ? error.message : String(error)
    ;(window as unknown as { __reportError?: string }).__reportError = message
    this.props.port.send({ type: 'views:error', message })
  }
  render() { return this.state.failed ? null : this.props.children }
}

function FrameRoot({ port, name, initial }: { port: HostPort; name: string; initial: ViewProps }) {
  const entry = registered(name)
  if (!entry) throw new Error(`The app registers no view named '${name}' (it registers: ${registeredNames().join(', ') || 'none'}).`)
  return createElement(ViewHost, { port, props: initial, children: createElement(Mounted, { component: entry.component }) })
}

/** The registered component, given the host's props as its own props (and through `useView`), drawn again whenever the host sends them. */
function Mounted({ component }: { component: ViewComponent }) {
  return createElement(component, host().props)
}

/** The view a host embedded in the page itself (a `page` exported as the top-level document): `<script type="application/json" id="dbdatakit-view-init">{"name": "RegionalDetail", "props": {...}}</script>`. Null when there is none. */
export function embeddedInit(doc: Document = document): { name: string; props: ViewProps } | null {
  const text = doc.getElementById('dbdatakit-view-init')?.textContent
  if (!text) return null
  try {
    const value = JSON.parse(text) as { name?: unknown; props?: unknown }
    if (typeof value.name !== 'string' || typeof value.props !== 'object' || value.props === null) return null
    return { name: value.name, props: value.props as ViewProps }
  } catch { return null }
}

/** A port for a page that has no host window above it (the top-level document an export prints): nothing to send to and nothing to hear; the view's `ready()` still sets the flags the exporter reads. */
export const standalonePort: HostPort = { send: () => {}, subscribe: () => () => {} }

/**
 * The frame's runtime. An app's entry calls it after its `defineWidget` / `definePage` calls: **in a view frame** (the address's fragment names a view, `frameRequest`) it waits for the host's `views:init`, mounts the
 * registered component with the props it gave (the host sends `views:props` on every change, and the component sees them through `ViewHost`), reports the content's height as `views:size` (the host sizes a page and
 * checks a widget against a printed page with it), and turns a render error into `views:error`; it returns true and the app must not mount its own screen. **In any other document** it does nothing and returns false.
 * **A page printed as the top-level document** has no host window: the host embeds the view's name and props in the page (`embeddedInit`), and the runtime mounts it at once with them and a port that talks to nobody.
 * The component calls `useReady().ready()` when it is drawn: the exporter waits for it.
 */
export function startViewFrame(options: { root?: HTMLElement; port?: HostPort; request?: ReturnType<typeof frameRequest> } = {}): boolean {
  const embedded = options.request === undefined ? embeddedInit() : null
  const request = options.request === undefined ? (embedded ? { view: embedded.name, mode: embedded.props.mode } : frameRequest()) : options.request
  if (!request) return false
  const port = options.port ?? (embedded ? standalonePort : windowPort())
  const root = options.root ?? (document.getElementById('dbdatakit-view') ?? document.body.appendChild(Object.assign(document.createElement('div'), { id: 'dbdatakit-view' })))
  let mounted: ReturnType<typeof createRoot> | null = null
  const mount = (name: string, props: ViewProps) => {
    if (mounted) return
    mounted = createRoot(root)
    mounted.render(createElement(FrameBoundary, { port, children: createElement(FrameRoot, { port, name, initial: { ...props, mode: request.mode } }) }))
    watchHeight(port)
  }
  port.subscribe((m) => { if (m.type === 'views:init') mount(m.name, m.props) })
  if (embedded) mount(embedded.name, embedded.props)
  else port.send({ type: 'views:hello' })
  return true
}

const contentHeight = () => Math.ceil(Math.max(document.documentElement?.scrollHeight ?? 0, document.body?.scrollHeight ?? 0))

function watchHeight(port: HostPort) {
  let last = -1
  const report = () => {
    const height = contentHeight()
    if (height !== last) { last = height; port.send({ type: 'views:size', height }) }
  }
  if (typeof ResizeObserver !== 'undefined') new ResizeObserver(report).observe(document.documentElement)
  report()
}
