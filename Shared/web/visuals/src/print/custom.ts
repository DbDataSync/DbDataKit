import type { FrameState } from '../custom/controller'
import type { ViewsDoc } from '../views/model'
import type { RenderedWidgets } from '../views/rendered'

/**
 * What stops the export of a custom widget (DBR32): a query that failed, a frame that failed or never said it was ready, a component the page has no files for, and **a widget whose content is taller than a page**. Each names the widget.
 * The widget's own box clips what overflows it, but a page cut can never show content taller than a page, so that is refused instead of printed cut.
 */
export function customProblem(document: ViewsDoc, rendered: RenderedWidgets, ids: string[], states: Record<string, FrameState>, sizes: Record<string, number>, pageHeight: number): string | null {
  for (const id of ids) {
    const result = rendered.widgets[id]
    const component = (document.widgets[id] as { component?: string } | undefined)?.component ?? id
    if (result?.type === 'error') return `The custom widget \`${id}\` (${component}) could not be resolved (${result.code}): ${result.message}`
    const state = states[id]
    if (state?.kind === 'error') return `The custom widget \`${id}\` (${component}) failed: ${state.message}`
    if (state === undefined) return `The custom widget \`${id}\` (${component}) was not drawn: the host has no app files for it.`
    const height = sizes[id]
    if (height !== undefined && height > pageHeight) return `The custom widget \`${id}\` (${component}) is taller than a page: its content is ${height}px high and a page holds ${pageHeight}px. Make it shorter, or make it a \`page\` view, which paginates.`
  }
  return null
}
