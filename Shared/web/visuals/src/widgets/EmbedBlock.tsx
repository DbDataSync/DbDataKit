import { useEffect, useState } from 'react'
import type { EmbedDoc } from '../views/model'
import type { RenderedEmbed } from '../views/rendered'
import { ErrorTile } from './states'

/**
 * An `embed` widget (plan 32 DBR19): an external report server's report. The server says whether the host approves the address's origin to be framed (`framed`); a frame is drawn only then, and only for an https address
 * with no credentials, from a sandboxed iframe that sends no referrer, so the report server sees the person's own browser sign in to it and nothing of this page. Otherwise (or without a resolution: the widget alone) it is
 * a link that opens in a new tab with no opener. Any address that is not plain https is an error tile and nothing of it is drawn.
 */
export const UNSAFE_EMBED_CODE = 'DBD-VIEW-038'

/** Whether an address may be framed or linked: an absolute https address with no credentials and no whitespace. */
export function isSafeEmbedUrl(url: string): boolean {
  if (url.length === 0 || url.length > 2000 || /[\s\u0000-\u001f]/.test(url)) return false
  try {
    const parsed = new URL(url)
    return parsed.protocol === 'https:' && parsed.username === '' && parsed.password === '' && parsed.hostname !== ''
  } catch { return false }
}

/** What the widget alone says: its address and literal pairs, as a link (the parameters and the approved origins are the server's to apply). */
export function embedOf(widget: EmbedDoc): RenderedEmbed {
  return { type: 'embed', url: widget.url, framed: false, ...(widget.link_text ? { link_text: widget.link_text } : {}) }
}

/**
 * How long a frame has to load before the widget gives up on it (plan 26 AP17). A browser tells the page nothing about a cross-origin frame that failed to sign in (a 401 challenge, a refused frame, a report server that
 * is not reachable from the person's network): no event, no status, no readable content. What the page can know is that the frame's `load` event did not come, so after this long without it the frame is replaced by the link.
 * A frame that loaded a sign-in failure page cannot be told from a report, which is why a framed report always carries its link below it.
 */
export const EMBED_FRAME_TIMEOUT_MS = 15_000

export function EmbedBlock({ widget, result, timeoutMs = EMBED_FRAME_TIMEOUT_MS }: { widget: EmbedDoc; result?: RenderedEmbed; timeoutMs?: number }) {
  const shown = result ?? embedOf(widget)
  const safe = isSafeEmbedUrl(shown.url)
  const framed = safe && shown.framed
  const [state, setState] = useState<'loading' | 'loaded' | 'failed'>('loading')
  useEffect(() => {
    if (!framed) return
    setState('loading')
    const timer = setTimeout(() => setState((now) => (now === 'loading' ? 'failed' : now)), timeoutMs)
    return () => clearTimeout(timer)
  }, [framed, shown.url, timeoutMs])
  if (!safe) return <ErrorTile code={UNSAFE_EMBED_CODE} message="An embedded report is an https address with no credentials." />
  const title = widget.title ?? 'Embedded report'
  const words = shown.link_text ?? widget.link_text ?? `Open ${widget.title ?? 'the report'}`
  if (framed && state !== 'failed')
    return (
      <div className="vz-embed" data-testid="vz-embed" data-mode="frame" data-state={state}>
        <iframe className="vz-embed-frame" title={title} src={shown.url} sandbox="allow-scripts allow-same-origin allow-forms allow-popups" referrerPolicy="no-referrer" loading="lazy" onLoad={() => setState('loaded')} />
        <p className="vz-embed-note" data-testid="vz-embed-open">
          <a href={shown.url} target="_blank" rel="noopener noreferrer">{words}</a>
        </p>
      </div>
    )
  return (
    <div className="vz-embed" data-testid="vz-embed" data-mode="link" data-state={framed ? 'failed' : 'link'}>
      {framed && <p className="vz-embed-failed" role="status" data-testid="vz-embed-failed">Cannot show this report here: the report server did not answer in the frame (it may need Windows sign-in from this network). Open it on the report server instead.</p>}
      <a href={shown.url} target="_blank" rel="noopener noreferrer">{words}</a>
    </div>
  )
}
