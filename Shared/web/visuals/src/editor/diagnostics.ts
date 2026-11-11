import type { ClientDiagnostic } from '../client/types'
import { pathSteps } from '../client/text'

/** What a diagnostic is about in the editor's forms: the document itself, a parameter, a widget (and the field of it), a layout (and its tile, and the field). */
export type FormTarget =
  | { area: 'document'; field: string }
  | { area: 'parameter'; index: number; field: string }
  | { area: 'widget'; id: string; field: string }
  | { area: 'layout'; index: number; tile?: number; field: string }

/** The form path of a diagnostic's key path (`layouts[1].tiles[0].x`): the field well, the tile or the layout the message belongs beside. */
export function formTarget(path: string): FormTarget {
  const steps = pathSteps(path)
  const [first, second, third, fourth] = steps
  const rest = (from: number) => steps.slice(from).join('.')
  if (first === 'widgets' && typeof second === 'string') return { area: 'widget', id: second, field: rest(2) }
  if (first === 'parameters' && typeof second === 'number') return { area: 'parameter', index: second, field: rest(2) }
  if (first === 'layouts' && typeof second === 'number') {
    if (third === 'tiles' && typeof fourth === 'number') return { area: 'layout', index: second, tile: fourth, field: rest(4) }
    return { area: 'layout', index: second, field: rest(2) }
  }
  return { area: 'document', field: steps.join('.') }
}

export interface EditorMarker { line: number; column: number; message: string; severity: 'error' | 'warning' }

/** The Monaco marker of a diagnostic (the shape of the shell's `EditorDiagnostic`); a diagnostic with no place in the text is put at the first line. */
export function toMarker(d: ClientDiagnostic): EditorMarker {
  return { line: d.line ?? 1, column: d.column ?? 1, message: `${d.code} ${d.message}`, severity: d.severity }
}

export const markersOf = (diagnostics: ClientDiagnostic[]): EditorMarker[] => diagnostics.map(toMarker)

const same = (a: FormTarget, b: Partial<FormTarget> & { area: FormTarget['area'] }) => Object.entries(b).every(([k, v]) => (a as unknown as Record<string, unknown>)[k] === v)

/** The diagnostics that belong to one place in the forms, e.g. `diagnosticsFor(ds, {area: 'widget', id: 'net'})`. */
export function diagnosticsFor(diagnostics: ClientDiagnostic[], where: Partial<FormTarget> & { area: FormTarget['area'] }): ClientDiagnostic[] {
  return diagnostics.filter((d) => same(formTarget(d.path), where))
}
