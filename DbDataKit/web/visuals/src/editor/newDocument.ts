import { canonicalText } from './canonical'
import type { ViewKind } from '../views/document'

/** The text of a new, empty document (plan 32 M2, step 1): an empty `desk` reflow layout (12 columns, no widgets), the default; a report starts with a flow layout instead, with a page setup and one section. */
export function newDocumentText(kind: ViewKind, title: string): string {
  if (kind === 'report')
    return canonicalText({
      schemaVersion: 1, kind, title, widgets: {},
      layouts: [{ name: 'paged', type: 'flow', default: true, page: { size: 'A4', orientation: 'portrait', margins: { top: 15, right: 15, bottom: 15, left: 15 } }, sections: [{ title: 'Summary', blocks: [] }] }],
    })
  return canonicalText({
    schemaVersion: 1, kind, title, widgets: {},
    layouts: [{ name: 'desk', type: 'reflow', default: true, grid: { columns: 12 }, tiles: [] }],
  })
}
