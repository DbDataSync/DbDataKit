import type { MouseEvent } from 'react'

/**
 * What an attribute cell may carry beyond its text (plan 32 DBR51), as the server wrote it: an `icon` of the closed vocabulary (chosen by the cell's value), a `badge`, a `mixed` marker and the `values` it stands
 * for (a tooltip), and the name of the document `link` a click on it follows. A measure's cell has none of these.
 */
export interface AttributeCellParts { icon?: string; badge?: boolean; mixed?: boolean; values?: string[]; link?: string }

/**
 * The glyph of each icon of the closed vocabulary. The bundled icon font (plan 32 DBR37) is not built; these are the text-presentation characters every system font has, drawn with the cell's own text as the
 * accessible name, so a page that loses the glyph still says the value. The names are the core schema's `icon` (a test keeps the two equal).
 */
export const ICON_GLYPHS: Record<string, string> = {
  circle: '○', dot: '●', check: '✓', cross: '✗', warning: '⚠', info: 'ℹ', clock: '⏲', flag: '⚑', star: '★', pause: '⏸', play: '▶',
  stop: '■', lock: '⚿', unlock: '•', 'arrow-up': '↑', 'arrow-down': '↓', 'arrow-right': '→', plus: '+', minus: '−', pin: '⌖', bell: '␇', eye: '◉', user: '☺', tag: '#',
}

/**
 * The inside of a cell: its text, or the icon, badge or mixed marker the server chose, and, when the cell names a link and the host follows links, a button that fires it (`onLink`). Nothing is computed here: the
 * server decided what the cell shows (the one-row rule of an attribute in a matrix); this draws it.
 */
export function CellContent({ cell, onLink }: { cell: { text: string } & AttributeCellParts; onLink?: (link: string, event: MouseEvent) => void }) {
  let inner
  if (cell.icon !== undefined) inner = <span className="vz-icon" role="img" aria-label={cell.text} title={cell.text} data-icon={cell.icon}>{ICON_GLYPHS[cell.icon] ?? '●'}</span>
  else if (cell.badge) inner = <span className="vz-badge">{cell.text}</span>
  else if (cell.mixed) inner = <span className="vz-mixed" title={(cell.values ?? []).join(', ')}>{cell.text}</span>
  else if (cell.values !== undefined) inner = <span title={cell.values.join(', ')}>{cell.text}</span>
  else return cell.link !== undefined && onLink !== undefined && cell.text.length > 0 ? link(cell.text, cell.link, onLink) : <>{cell.text}</>
  return cell.link !== undefined && onLink !== undefined ? link(inner, cell.link, onLink) : inner
}

const link = (content: React.ReactNode, name: string, onLink: (link: string, event: MouseEvent) => void) => (
  <button type="button" className="vz-cell-link" data-link={name} onClick={(e) => { e.stopPropagation(); onLink(name, e) }}>{content}</button>
)
