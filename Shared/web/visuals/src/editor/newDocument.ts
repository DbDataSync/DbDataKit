import { canonicalText } from './canonical'
import type { ViewKind } from '../views/document'

const NOTE = { type: 'text', text: 'Write a note.' }

/** The text of a new document (plan 32 M2, step 1): a `desk` reflow layout (12 columns), the default; a report starts with a flow layout instead, with a page setup and one section. It is valid as it stands (the loader needs a widget and a place for it): one text widget, `note`, to replace. */
export function newDocumentText(kind: ViewKind, title: string): string {
  if (kind === 'report')
    return canonicalText({
      schemaVersion: 1, kind, title, widgets: { note: NOTE },
      layouts: [{ name: 'paged', type: 'flow', default: true, page: { size: 'A4', orientation: 'portrait', margins: { top: 15, right: 15, bottom: 15, left: 15 } }, sections: [{ title: 'Summary', blocks: [{ widget: 'note' }] }] }],
    })
  return canonicalText({
    schemaVersion: 1, kind, title, widgets: { note: NOTE },
    layouts: [{ name: 'desk', type: 'reflow', default: true, grid: { columns: 12 }, tiles: [{ widget: 'note', x: 0, y: 0, w: 12, h: 1 }] }],
  })
}
