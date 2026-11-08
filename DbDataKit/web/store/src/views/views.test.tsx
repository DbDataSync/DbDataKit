import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { CopyState, MigrationPlan, StoragePlan } from '../api'
import { DiagnosticList, ModelEditorView, type EditorViewProps } from './ModelEditorView'
import { CopyView, type CopyViewProps } from './StorageView'

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

const plan = (existing: string[] | null, over: Partial<StoragePlan> = {}): StoragePlan => ({ engine: 'postgres', statements: [{ object: 't', description: 'create table', text: 'CREATE TABLE t' }], existing, ...over })
const state = (status: string): CopyState => ({ name: 'default', definition: 'x', applied_at: null, status })
const copy = (over: Partial<CopyViewProps> = {}): CopyViewProps => ({ name: 'default', plan: plan([]), state: undefined, dry: null, done: null, refusal: null, blocked: null, busy: false, onCreate: none, onDryRun: none, onApply: none, ...over })

describe('the storage view', () => {
  it('offers Create tables when there are none, with the statements it would run', () => {
    const html = renderToStaticMarkup(<CopyView {...copy()} />)
    expect(html).toContain('data-action="create"')
    expect(html).toContain('data-testid="storage-create"')
    expect(html).toContain('CREATE TABLE t')
    expect(html).not.toContain('storage-dry-run')
  })
  it('offers a dry run when the copy is behind, and Apply only after a dry run that can be applied', () => {
    const behind = renderToStaticMarkup(<CopyView {...copy({ plan: plan(['t']), state: state('behind') })} />)
    expect(behind).toContain('Behind the model')
    expect(behind).toContain('data-testid="storage-dry-run"')
    expect(behind).not.toContain('storage-apply')
    const dry: MigrationPlan = { up_to_date: false, applied: false, statements: [{ object: 't', description: 'add column', text: 'ALTER TABLE t ADD c' }], blocked: [], notes: ['kept: x'] }
    const after = renderToStaticMarkup(<CopyView {...copy({ plan: plan(['t']), state: state('behind'), dry })} />)
    expect(after).toMatch(/<button[^>]*data-testid="storage-apply"/)
    expect(after).not.toMatch(/<button[^>]*disabled=""[^>]*data-testid="storage-apply"/)
    expect(after).toContain('ALTER TABLE t ADD c')
    expect(after).toContain('kept: x')
  })
  it('a blocked migration cannot be applied and lists what blocks it', () => {
    const dry: MigrationPlan = { up_to_date: false, applied: false, statements: [], blocked: ['column c would become NOT NULL'], notes: [] }
    const html = renderToStaticMarkup(<CopyView {...copy({ plan: plan(['t']), state: state('behind'), dry })} />)
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*data-testid="storage-apply"/)
    expect(html).toContain('column c would become NOT NULL')
  })
  it('every change is off with the reason when the role or the model forbids it', () => {
    const html = renderToStaticMarkup(<CopyView {...copy({ blocked: 'Creating tables and migrating them needs the administrator role.' })} />)
    expect(html).toMatch(/<button[^>]*disabled=""[^>]*data-testid="storage-create"/)
    expect(html).toContain('needs the administrator role')
  })
  it('shows a connection that could not be opened instead of offering anything', () => {
    const html = renderToStaticMarkup(<CopyView {...copy({ plan: plan(null, { connection_problem: 'mdm.yaml has no login for the server to write with' }) })} />)
    expect(html).toContain('data-testid="storage-connection-problem"')
    expect(html).not.toContain('storage-create"')
  })
})

