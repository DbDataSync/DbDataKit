import type { AdminHost } from '@dbdatakit/admin'
import { ConsoleShell } from './ConsoleShell'

/**
 * What the console tells the shared admin screens: its frame. It edits no driver text and ships no docs of its own yet, so the code editor is a plain text box that
 * no screen the console routes to will ever show.
 */
export const adminHost: AdminHost = {
  Frame: ConsoleShell,
  CodeEditor: ({ value, onChange, readOnly, testId }) => (
    <textarea className="input mono" value={value} readOnly={readOnly} data-testid={testId} onChange={(e) => onChange(e.target.value)} />
  ),
}
