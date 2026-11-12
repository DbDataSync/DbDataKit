import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react'
import { describeLink, type LinkDoc, type LinkInfo } from '../views/links'
import type { Box, CustomDoc, ParameterDefinition, ParameterValue, ParameterValues } from '../views/model'
import type { RenderedCustom } from '../views/rendered'
import { EmptyTile, ErrorTile } from '../widgets/states'
import { createFrameHost, type FrameHost, type FrameState } from './controller'
import { hostLinkInfo, readToHost, themeTokens, type Row, type ViewMode, type ViewProps } from './protocol'

/**
 * What a host gives so custom widgets can be drawn (plan 32 DBR31): where the code of an app is served from. The host that serves the app (the apps shell, a Workspace preview) says; a host that serves none leaves
 * the context out, and a custom widget draws its note ("Drawn by the app that owns it") instead.
 */
export interface CustomViewsHost {
  /** The address of the app's built page that mounts the views (`#view=<name>&mode=<mode>` is added), or null for a component this host does not serve. */
  frameSrc(component: string): string | null
  /** The address of a link's target inside the app (a page or a view the app serves), or null for one with no address of its own. */
  route?(link: LinkInfo): string | null
  /** Each frame's state as it changes (the print page waits for every frame to be ready, or fails on the first error). */
  onFrameState?(widget: string, state: FrameState): void
  /** The height of each frame's content, as the frame reports it (the print page checks it against a page). */
  onFrameSize?(widget: string, height: number): void
  /** How long a frame may take to say hello before its widget fails (milliseconds; 15 s), and, off the screen, to say it is ready (30 s). */
  startTimeoutMs?: number
  readyTimeoutMs?: number
}

export interface CustomViewsContextValue {
  host: CustomViewsHost
  /** The document's `links` by name. */
  links: Record<string, LinkDoc> | undefined
  definitions: ParameterDefinition[] | undefined
  parameters: ParameterValues
  medium: ViewMode
  scheme: 'light' | 'dark'
  /** A parameter the code asked to set, already checked against the document: the dashboard changes it as a control would. */
  setParam(name: string, value: ParameterValue): void
  /** A link the code followed for a row: the dashboard resolves its target and values. */
  open(widget: string, link: LinkInfo, row: Row): void
  notice(message: string): void
  /** A dialog or panel the code opened has closed (a new object each time). */
  closed: { widget: string; link: string; saved: boolean } | null
}

export const CustomViewsContext = createContext<CustomViewsContextValue | null>(null)

export interface CustomWidgetFrameProps { id: string; widget: CustomDoc; result: RenderedCustom; box?: Box }

/**
 * A custom widget in its tile: a sandboxed frame (`sandbox="allow-scripts"` and nothing else, so an opaque origin: no cookies, no storage, no parent access) loaded from the app's own files, which the host
 * gives the rows its declared queries returned, the widget's parameters, the theme and the box. What comes back is read strictly (`readToHost`, only from this frame's window) and answered by the document's rules
 * (`createFrameHost`). A view that fails, or that never starts, shows an error tile; the other widgets are not touched.
 */
export function CustomWidgetFrame({ id, widget, result, box }: CustomWidgetFrameProps) {
  const ctx = useContext(CustomViewsContext)
  const src = ctx?.host.frameSrc(widget.component) ?? null
  if (!ctx || src === null) return <EmptyTile message={`Drawn by the app that owns it (${widget.component}).`} />
  return <Frame ctx={ctx} src={src} id={id} widget={widget} result={result} box={box} />
}

const NO_PARAMS: ViewProps['params'] = {}
const exportOf = (component: string) => component.slice(component.lastIndexOf('/') + 1)

function Frame({ ctx, src, id, widget, result, box }: CustomWidgetFrameProps & { ctx: CustomViewsContextValue; src: string }) {
  const iframe = useRef<HTMLIFrameElement>(null)
  const [state, setState] = useState<FrameState>({ kind: 'loading' })
  const live = useRef(ctx)
  live.current = ctx

  const linkNames = widget.links ?? []
  const links = useMemo(() => {
    const out: ViewProps['links'] = {}
    for (const name of linkNames) {
      const doc = ctx.links?.[name]
      if (doc) out[name] = hostLinkInfo(describeLink(name, doc), ctx.host.route?.(describeLink(name, doc)) ?? null)
    }
    return out
    // the links change with the document, not with every render
  }, [ctx.links, ctx.host, linkNames.join('\n')])

  // what the widget's parameters have now was resolved with the rows (a changed document parameter resolves the widget again)
  const params = (result.params ?? NO_PARAMS) as ViewProps['params']

  const props: ViewProps = useMemo(() => ({
    data: result.data,
    params,
    theme: { scheme: ctx.scheme, tokens: themeTokens(ctx.scheme) },
    size: { width: box?.width ?? 0, height: box?.height ?? 'auto' },
    mode: ctx.medium,
    links,
  }), [result.data, params, ctx.scheme, ctx.medium, box?.width, box?.height, links])
  const latestProps = useRef(props)
  latestProps.current = props

  const host = useRef<FrameHost | null>(null)
  useEffect(() => {
    const frame = iframe.current
    if (!frame) return
    const controller = createFrameHost({
      name: exportOf(widget.component),
      post: (m) => frame.contentWindow?.postMessage(m, '*'),
      props: () => latestProps.current,
      definitions: live.current.definitions,
      openable: new Set(linkNames.filter((n) => live.current.links?.[n])),
      onSetParam: (name, value) => live.current.setParam(name, value),
      onOpen: (link, row) => {
        const doc = live.current.links?.[link]
        if (doc) live.current.open(id, describeLink(link, doc), row)
      },
      onState: (s) => { setState(s); live.current.host.onFrameState?.(id, s) },
      onSize: (h) => live.current.host.onFrameSize?.(id, h),
      onNotice: (m) => live.current.notice(m),
    })
    host.current = controller
    const listen = (e: MessageEvent) => {
      // only this frame's window, and only the shapes of the protocol
      if (e.source !== frame.contentWindow) return
      const message = readToHost(e.data)
      if (message) controller.receive(message)
    }
    window.addEventListener('message', listen)
    const fail = (message: string) => { const s: FrameState = { kind: 'error', message }; setState(s); live.current.host.onFrameState?.(id, s) }
    // a frame that says nothing fails its widget; off the screen (print, a picture) it must also say it is ready, which is what the exporter waits for
    const startTimer = window.setTimeout(() => {
      if (!controller.started) fail(`The view \`${widget.component}\` did not start: the app's page loaded nothing that registers it (it must call startViewFrame of @dbdatakit/views-sdk).`)
    }, live.current.host.startTimeoutMs ?? 15000)
    const readyTimer = live.current.medium === 'screen' ? 0 : window.setTimeout(() => {
      if (controller.state.kind === 'loading') fail(`The view \`${widget.component}\` did not say it was ready: its code must call \`ready()\` of useReady when it is drawn.`)
    }, live.current.host.readyTimeoutMs ?? 30000)
    return () => { window.removeEventListener('message', listen); window.clearTimeout(startTimer); window.clearTimeout(readyTimer); host.current = null }
    // a frame is made once per widget and component; the props reach it through `latestProps`
  }, [id, widget.component, src, linkNames.join('\n')])

  useEffect(() => { host.current?.sendProps() }, [props])
  useEffect(() => {
    const c = ctx.closed
    if (c && c.widget === id) host.current?.sendClosed(c.link, c.saved)
  }, [ctx.closed, id])

  const address = `${src}#view=${encodeURIComponent(exportOf(widget.component))}&mode=${ctx.medium}`
  if (state.kind === 'error') return <ErrorTile code="custom_view_failed" message={state.message} />
  return (
    <iframe
      ref={iframe}
      className="vz-custom-frame"
      data-testid={`vz-custom-frame-${id}`}
      data-state={state.kind}
      title={widget.title ?? widget.component}
      sandbox="allow-scripts"
      referrerPolicy="no-referrer"
      src={address}
      style={{ border: 0, display: 'block', width: box ? box.width : '100%', height: box ? box.height : '100%', background: 'transparent' }}
    />
  )
}
