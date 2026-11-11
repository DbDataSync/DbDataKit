import type { ClientDiagnostic } from './types'

/**
 * The module's refusal of a document, as diagnostics. A publish's validator says what is wrong as one text, each finding `file:line:column: error <code>: <message> [<key path>]` joined by ` | `
 * (`ViewDefinitions.Validate`); the preview route repeats it as the `message` of a 400 `invalid`. The editor places findings by key path, so this reads the parts back; a part it cannot read is kept as it is
 * (a diagnostic with no path, shown with the others), so nothing the module said is lost.
 */
export function diagnosticsFromRefusal(message: string, file?: string): ClientDiagnostic[] {
  const body = message.replace(/^The \w+ does not load: /, '')
  return body.split(' | ').map((part) => part.trim()).filter((part) => part !== '').map((part): ClientDiagnostic => {
    const m = /^(?:[^\s:][^:]*:(\d+):(\d+): )?error ([^\s:]+): (.*?)(?: \[([^\]]+)\])?$/s.exec(part)
    if (!m) return { code: 'DBD-VIEW-SERVER', severity: 'error', path: '', message: part, file }
    return {
      code: m[3]!, severity: 'error', path: m[5] ?? '', message: m[4]!, file,
      ...(m[1] !== undefined ? { line: Number(m[1]), column: Number(m[2]) } : {}),
    }
  })
}
