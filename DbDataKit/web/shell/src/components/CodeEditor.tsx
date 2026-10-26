import { Suspense, lazy } from 'react'
/** A problem to mark in the text: where it is (line 0 when it has no position) and what it says. */
export interface EditorDiagnostic {
  line: number
  column: number
  message: string
  /** Defaults to an error. */
  severity?: 'error' | 'warning' | 'info'
}

export interface CodeEditorProps {
  value: string
  // 'yaml' added for the driver-authoring form's own raw dialect/typeMap editor — the tokenizer was
  // already registered (monacoSetup.ts, phase 35) for read-only config diffs; this is its first
  // *editable* use. No live diagnostics for it the way script editing gets them (no compile endpoint
  // to check a driver.yaml against as you type) — errors surface on Save instead.
  language: 'csharp' | 'sql' | 'yaml' | 'plaintext'
  onChange: (value: string) => void
  /** Server-side compile or validation errors, shown as markers on the offending token. */
  diagnostics?: EditorDiagnostic[]
  readOnly?: boolean
  minLines?: number
  maxLines?: number
  testId?: string
}

/**
 * Monaco, loaded only when a screen actually shows an editor.
 *
 * The whole editor — API, theme, worker and the two language tokenizers — is behind this dynamic
 * import, so the replications list (which most sessions never leave) does not pay for it. The fallback
 * reserves the same height, so the card does not jump when the chunk lands.
 */
const MonacoEditor = lazy(() => import('./code/MonacoEditor'))

export function CodeEditor(props: CodeEditorProps) {
  const lines = Math.max(props.minLines ?? 3, props.value.split('\n').length + 1)
  return (
    <Suspense fallback={<div className="code-editor loading" style={{ height: lines * 19 + 16 }} />}>
      <MonacoEditor {...props} />
    </Suspense>
  )
}
