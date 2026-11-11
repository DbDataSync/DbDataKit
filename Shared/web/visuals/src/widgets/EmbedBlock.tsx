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

export function EmbedBlock({ widget, result }: { widget: EmbedDoc; result?: RenderedEmbed }) {
  const shown = result ?? embedOf(widget)
  if (!isSafeEmbedUrl(shown.url)) return <ErrorTile code={UNSAFE_EMBED_CODE} message="An embedded report is an https address with no credentials." />
  const title = widget.title ?? 'Embedded report'
  const words = shown.link_text ?? widget.link_text ?? `Open ${widget.title ?? 'the report'}`
  if (shown.framed)
    return (
      <div className="vz-embed" data-testid="vz-embed" data-mode="frame">
        <iframe className="vz-embed-frame" title={title} src={shown.url} sandbox="allow-scripts allow-same-origin allow-forms allow-popups" referrerPolicy="no-referrer" loading="lazy" />
      </div>
    )
  return (
    <div className="vz-embed" data-testid="vz-embed" data-mode="link">
      <a href={shown.url} target="_blank" rel="noopener noreferrer">{words}</a>
    </div>
  )
}
