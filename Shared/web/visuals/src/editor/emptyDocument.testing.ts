import { canonicalText } from './canonical'
import type { ViewKind } from '../views/document'

/** A document with no widget and no place for one (what `newDocumentText` made before it became valid as it stands): the starting point of the tests that build a document up from nothing. It is invalid until a widget is placed. */
export function emptyDocumentText(kind: ViewKind, title: string): string {
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
