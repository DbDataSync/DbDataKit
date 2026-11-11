import type { EditorDiagnostic } from '@dbdatakit/shell'
import type { CheckDiagnostic } from './api'

/** The structured editors a draft's file can have beside the plain text one (plan 33 SC21d, SC22d): a form made from a schema, over the same text as the YAML. */
export type EditorKind = 'store-model' | 'sync-task' | 'sync-mapping' | 'sync-connection' | 'integration-manifest'

/** Which structured editor a file of a project of this kind gets, by where the kind keeps it; null for a file that is edited as text only. */
export function editorKindOf(projectKind: string | undefined, path: string): EditorKind | null {
  if (!/\.ya?ml$/i.test(path)) return null
  if (projectKind === 'store') return /^models\/(?:[^/]+\/)*[^/]+$/.test(path) ? 'store-model' : null
  if (projectKind === 'integration') return path === 'package.yaml' ? 'integration-manifest' : null
  if (projectKind === 'sync') {
    if (/^config\/replications\/[^/]+\/task\.ya?ml$/i.test(path)) return 'sync-task'
    if (/^config\/replications\/[^/]+\/table-mappings\/[^/]+$/.test(path)) return 'sync-mapping'
    if (/^config\/connections\/[^/]+$/.test(path)) return 'sync-connection'
  }
  return null
}

/** What the code editor marks for a file: the findings of the check that name this file (or no file at all), a finding with no line on the first line. */
export function markersFor(path: string, diagnostics: readonly CheckDiagnostic[]): EditorDiagnostic[] {
  return diagnostics.filter((d) => d.file === path || d.file === null).map((d) => ({ line: d.line && d.line > 0 ? d.line : 1, column: 1, message: `${d.code}: ${d.message}`, severity: d.severity }))
}

/** The findings of the file itself, and apart those about other files (each has a button in the Check tab). */
export function splitFindings(path: string, diagnostics: readonly CheckDiagnostic[]): { here: CheckDiagnostic[]; elsewhere: CheckDiagnostic[] } {
  const here = diagnostics.filter((d) => d.file === path || d.file === null)
  return { here, elsewhere: diagnostics.filter((d) => !here.includes(d)) }
}

export type LiveState = 'checking' | 'stale' | 'failed' | 'current'

/** How far the check beside an editor has got: the answer is about `checkedText`, which is not the text on screen once the person types on. */
export function liveState(input: { text: string; checkedText: string | null; failed: string | null; running: boolean }): LiveState {
  if (input.failed) return 'failed'
  if (input.running) return 'checking'
  return input.checkedText === input.text ? 'current' : 'stale'
}

/** The words for the check beside an editor, by how far it has got. */
export function liveWord(state: LiveState, errors: number, warnings: number): string {
  if (state === 'checking') return 'Saving to the draft and checking...'
  if (state === 'stale') return 'Checking a moment after you stop typing.'
  if (state === 'failed') return 'The check did not run.'
  const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`
  if (errors > 0) return `${plural(errors, 'problem')} to fix${warnings ? `, ${plural(warnings, 'warning')}` : ''}.`
  return warnings > 0 ? `Sound, with ${plural(warnings, 'warning')}.` : 'The draft is sound.'
}
