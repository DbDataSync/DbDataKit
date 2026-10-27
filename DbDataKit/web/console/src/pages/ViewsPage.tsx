import { useState } from 'react'
import { RenderedViews, validateViewDocument, type ThemeChoice } from '@dbdatakit/visuals'
import { ConsoleShell } from '../ConsoleShell'

type Opened =
  | { kind: 'none' }
  | { kind: 'rendered'; document: unknown }
  | { kind: 'message'; text: string; problems?: string[] }

/** What a person pasted or opened: a rendered view is shown; a view document is checked and explained; anything else is said not to be a view. */
export function open(text: string): Opened {
  let document: unknown
  try {
    document = JSON.parse(text)
  } catch (e) {
    return { kind: 'message', text: `That is not JSON: ${e instanceof Error ? e.message : String(e)}` }
  }
  const kind = typeof document === 'object' && document !== null ? (document as { kind?: unknown }).kind : undefined
  if (kind === 'rendered_view') return { kind: 'rendered', document }
  if (kind === 'view') {
    const issues = validateViewDocument(document)
    if (issues.length > 0) return { kind: 'message', text: 'This view document is not valid.', problems: issues.map((i) => `${i.code} ${i.path}: ${i.message}`) }
    return { kind: 'message', text: 'This is a view document: it says how to show a result, and it needs that result. The console shows the rendered view the semantic layer makes from a view document and its result; open that.' }
  }
  return { kind: 'message', text: 'This is neither a rendered view nor a view document (its "kind" is "rendered_view" or "view").' }
}

/**
 * The shared visuals in the console (plan 19, Y03): open a rendered view document, from a file or pasted, and see it. The same renderer an app or an assistant's chat uses
 * draws it; nothing here draws or checks anything of its own. The semantic module (J14) will fetch these from a query instead of a file.
 */
export function ViewsPage() {
  const [text, setText] = useState('')
  const [opened, setOpened] = useState<Opened>({ kind: 'none' })
  const [theme, setTheme] = useState<ThemeChoice>('light')

  return (
    <ConsoleShell crumbs={[{ label: 'Views', heading: true }]}>
      <div className="pane" data-testid="views-page">
        <div className="page-head">
          <h1 className="page-title">Views</h1>
          <span className="page-note">Open a rendered view document and see it as a table or a chart.</span>
        </div>
        <div className="card" style={{ marginBottom: 16 }}>
          <label htmlFor="views-input">View document (JSON)</label>
          <textarea id="views-input" data-testid="views-input" value={text} rows={8} spellCheck={false} style={{ width: '100%', fontFamily: 'var(--mono)', fontSize: 12 }} onChange={(e) => setText(e.target.value)} />
          <div className="row" style={{ gap: 8, marginTop: 8, alignItems: 'center' }}>
            <button type="button" className="btn btn-primary" data-testid="views-show" disabled={text.trim() === ''} onClick={() => setOpened(open(text))}>Show</button>
            <label className="btn" htmlFor="views-file">Open a file</label>
            <input
              id="views-file"
              type="file"
              accept=".json,application/json"
              data-testid="views-file"
              style={{ display: 'none' }}
              onChange={(e) => {
                const file = e.target.files?.[0]
                if (!file) return
                void file.text().then((content) => { setText(content); setOpened(open(content)) })
                e.target.value = ''
              }}
            />
            <label htmlFor="views-theme">Colours</label>
            <select id="views-theme" data-testid="views-theme" value={theme} onChange={(e) => setTheme(e.target.value as ThemeChoice)}>
              <option value="light">Light</option>
              <option value="dark">Dark</option>
              <option value="auto">Follow the system</option>
            </select>
          </div>
        </div>
        {opened.kind === 'message' && (
          <div className="empty" data-testid="views-message" role="status">
            {opened.text}
            {opened.problems && <ul>{opened.problems.map((p) => <li key={p}>{p}</li>)}</ul>}
          </div>
        )}
        {opened.kind === 'rendered' && <RenderedViews document={opened.document} theme={theme} showData />}
      </div>
    </ConsoleShell>
  )
}
