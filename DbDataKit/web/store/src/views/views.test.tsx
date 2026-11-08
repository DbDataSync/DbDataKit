import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { DiagnosticList, ModelEditorView, type EditorViewProps } from './ModelEditorView'

const none = () => {}
const editor = (over: Partial<EditorViewProps> = {}): EditorViewProps => ({
  isNew: false, name: 'm', mode: 'form', onMode: none, form: <i>the form</i>, yaml: <i>the yaml</i>, diff: <i>the diff</i>, showDiff: false, onShowDiff: none, dirty: true,
  diagnostics: [], checking: false, save: { allowed: true, reason: null }, saving: false, onSave: none, refusal: null, conflict: false, onReload: none, onOverwrite: none, savedNote: null, ...over,
})

describe('the model editor view', () => {
  it('shows the form or the YAML, never both, and the Store\'s check below', () => {
    expect(renderToStaticMarkup(<ModelEditorView {...editor()} />)).toContain('the form')
    const yaml = renderToStaticMarkup(<ModelEditorView {...editor({ mode: 'yaml' })} />)
    expect(yaml).toContain('the yaml')
    expect(yaml).not.toContain('the form')
    expect(yaml).toContain('data-testid="model-diagnostics"')
  })
  it('a button that is off says why beside itself, and is disabled', () => {
    const html = renderToStaticMarkup(<ModelEditorView {...editor({ save: { allowed: false, reason: 'The Store refuses this model: 1 problem to fix' }, remove: { allowed: false, reason: 'Removing a model needs the administrator role.' }, onRemove: none })} />)
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*data-testid="model-save"/)
    expect(html).toContain('data-testid="model-save-why">The Store refuses this model')
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*data-testid="model-remove"/)
    expect(html).toContain('needs the administrator role')
  })
  it('a new model asks for a name and offers Create, and no Remove and no diff', () => {
    const html = renderToStaticMarkup(<ModelEditorView {...editor({ isNew: true })} />)
    expect(html).toContain('data-testid="model-name-input"')
    expect(html).toContain('>Create model<')
    expect(html).not.toContain('model-remove')
    expect(html).not.toContain('model-diff-toggle')
  })
  it('shows a refusal in the Store\'s words and a stale save with both ways out', () => {
    const html = renderToStaticMarkup(<ModelEditorView {...editor({ refusal: 'm.yaml is not a valid model, so nothing was saved.', conflict: true })} />)
    expect(html).toContain('nothing was saved')
    expect(html).toContain('data-testid="model-conflict-reload"')
    expect(html).toContain('data-testid="model-conflict-overwrite"')
  })
  it('lists each diagnostic with its line and its fix, says when it is clean and when it is checking', () => {
    const d = { code: 'DBD-1', severity: 'error', file: 'm.yaml', line: 4, column: 1, message: 'Unknown type', fix: 'Use VARCHAR(n).' }
    const bad = renderToStaticMarkup(<DiagnosticList diagnostics={[d]} checking={false} />)
    expect(bad).toContain('line 4')
    expect(bad).toContain('Use VARCHAR(n).')
    expect(bad).toContain('data-state="invalid"')
    expect(renderToStaticMarkup(<DiagnosticList diagnostics={[]} checking={false} />)).toContain('The Store accepts this model.')
    expect(renderToStaticMarkup(<DiagnosticList diagnostics={[]} checking />)).toContain('Checking...')
  })
})

