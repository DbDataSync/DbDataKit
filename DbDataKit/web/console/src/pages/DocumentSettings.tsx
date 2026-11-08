import { useState } from 'react'
import { NAME_PATTERN, diagnosticsFor, type ClientDiagnostic, type EditCommand, type ParameterDefinition, type ViewsDoc, type WidgetDoc } from '@dbdatakit/visuals'

type Apply = (command: EditCommand, group?: string) => void

/** The text a default is shown as, and the value a typed text makes of it: a list is comma separated, a number a number, a boolean `true` or `false`; text that is not of the type is kept as typed (the loader says so). */
export function defaultText(p: ParameterDefinition): string {
  const d = p.default
  return d === undefined || d === null ? '' : Array.isArray(d) ? d.join(', ') : String(d)
}
export function parseDefault(p: Pick<ParameterDefinition, 'type' | 'many'>, text: string): unknown {
  const one = (t: string): unknown => (p.type === 'integer' || p.type === 'number' ? (t.trim() !== '' && Number.isFinite(Number(t)) ? Number(t) : t) : p.type === 'boolean' ? (t.trim() === 'true' ? true : t.trim() === 'false' ? false : t) : t)
  if (p.many) return text.split(',').map((t) => t.trim()).filter((t) => t !== '').map(one)
  return text.trim() === '' ? undefined : one(text.trim())
}

function Problems({ diagnostics, testId }: { diagnostics: ClientDiagnostic[]; testId: string }) {
  if (diagnostics.length === 0) return null
  return <ul className="hint" data-testid={testId} style={{ margin: '2px 0' }}>{diagnostics.map((d, i) => <li key={i} role="alert">{d.code} {d.message}</li>)}</ul>
}

const MARKS = ['bar', 'line', 'area', 'point', 'tick', 'rect']

/** The document's refresh, its parameters and the properties of its widgets (title, label, mark, text): small forms over `setPath` and its siblings, each keystroke of one field one undo step. */
export function DocumentSettings({ doc, diagnostics, apply }: { doc: ViewsDoc; diagnostics: ClientDiagnostic[]; apply: Apply }) {
  const [newName, setNewName] = useState('')
  const [newType, setNewType] = useState<ParameterDefinition['type']>('string')
  const parameters = doc.parameters ?? []
  const setProp = (id: string, key: string, text: string, exists: boolean) => {
    if (text === '' && !exists) return
    apply(text === '' ? { type: 'removePath', path: ['widgets', id, key] } : { type: 'setPath', path: ['widgets', id, key], value: text }, `prop:${id}:${key}`)
  }
  return (
    <>
      {doc.kind === 'report' ? <ReportSettings doc={doc} diagnostics={diagnostics} apply={apply} /> : (
      <div className="card" style={{ marginBottom: 12 }} data-testid="ed-document-settings">
          <strong>Refresh</strong>{' '}
          <label className="hint">every{' '}
            <input data-testid="ed-refresh" style={{ width: 80 }} placeholder="5m" value={doc.refresh?.every ?? ''} onChange={(e) => {
              const text = e.target.value
              if (text === '' && doc.refresh === undefined) return
              apply(text === '' ? { type: 'removePath', path: ['refresh'] } : { type: 'setPath', path: ['refresh', 'every'], value: text }, 'refresh')
            }} />
          </label>
          <Problems diagnostics={diagnosticsFor(diagnostics, { area: 'document' }).filter((d) => d.path.startsWith('refresh'))} testId="ed-refresh-problems" />
        </div>
      )}

      <div className="card" style={{ marginBottom: 12 }} data-testid="ed-parameters">
        <strong>Parameters</strong>
        <table className="table">
          <tbody>
            {parameters.map((p, i) => (
              <tr key={p.name} data-testid={`ed-parameter-${p.name}`}>
                <td><code>{p.name}</code></td>
                <td>{p.type}{p.many ? ' (list)' : ''}</td>
                <td><input aria-label={`${p.name} label`} data-testid={`ed-parameter-label-${p.name}`} placeholder="label" value={p.label ?? ''} onChange={(e) => apply(e.target.value === '' ? (p.label === undefined ? { type: 'setPath', path: ['parameters', i, 'name'], value: p.name } : { type: 'removePath', path: ['parameters', i, 'label'] }) : { type: 'setPath', path: ['parameters', i, 'label'], value: e.target.value }, `param:${p.name}:label`)} /></td>
                <td><input aria-label={`${p.name} default`} data-testid={`ed-parameter-default-${p.name}`} placeholder={p.many ? 'a, b' : 'default'} value={defaultText(p)} onChange={(e) => {
                  const value = parseDefault(p, e.target.value)
                  apply(value === undefined ? (p.default === undefined ? { type: 'setPath', path: ['parameters', i, 'name'], value: p.name } : { type: 'removePath', path: ['parameters', i, 'default'] }) : { type: 'setPath', path: ['parameters', i, 'default'], value }, `param:${p.name}:default`)
                }} /></td>
                <td>
                  <Problems diagnostics={diagnosticsFor(diagnostics, { area: 'parameter', index: i })} testId={`ed-parameter-problems-${p.name}`} />
                  <button type="button" className="btn btn-sm" data-testid={`ed-parameter-remove-${p.name}`} onClick={() => apply({ type: 'removeParameter', name: p.name })}>Remove</button>
                </td>
              </tr>
            ))}
            {parameters.length === 0 && <tr><td className="hint">No parameters.</td></tr>}
          </tbody>
        </table>
        <div className="row" style={{ gap: 6, alignItems: 'center' }}>
          <input aria-label="New parameter name" data-testid="ed-parameter-name" placeholder="region" value={newName} onChange={(e) => setNewName(e.target.value)} />
          <select aria-label="New parameter type" data-testid="ed-parameter-type" value={newType} onChange={(e) => setNewType(e.target.value as ParameterDefinition['type'])}>
            {(['string', 'integer', 'number', 'boolean'] as const).map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
          <button type="button" className="btn btn-sm" data-testid="ed-parameter-add" disabled={!NAME_PATTERN.test(newName)} onClick={() => { apply({ type: 'addParameter', parameter: { name: newName, type: newType } }); setNewName('') }}>Add parameter</button>
        </div>
      </div>

      <div className="card" style={{ marginBottom: 12 }} data-testid="ed-widget-properties">
        <strong>Widget properties</strong>
        {Object.entries(doc.widgets ?? {}).map(([id, w]) => {
          const widget = w as WidgetDoc & { title?: string; label?: string; mark?: unknown; text?: string }
          return (
            <div key={id} className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', padding: '4px 0' }} data-testid={`ed-props-${id}`}>
              <code style={{ minWidth: 90 }}>{id}</code>
              <label className="hint">title <input data-testid={`ed-prop-title-${id}`} value={widget.title ?? ''} onChange={(e) => setProp(id, 'title', e.target.value, widget.title !== undefined)} /></label>
              {(widget.type === 'metric' || widget.type === 'control') && <label className="hint">label <input data-testid={`ed-prop-label-${id}`} value={widget.label ?? ''} onChange={(e) => setProp(id, 'label', e.target.value, widget.label !== undefined)} /></label>}
              {widget.type === 'chart' && (
                <label className="hint">mark{' '}
                  <select data-testid={`ed-prop-mark-${id}`} value={typeof widget.mark === 'string' ? widget.mark : ''} onChange={(e) => apply({ type: 'setPath', path: ['widgets', id, 'mark'], value: e.target.value }, `prop:${id}:mark`)}>
                    {typeof widget.mark !== 'string' && <option value="">(see YAML)</option>}
                    {MARKS.map((m) => <option key={m} value={m}>{m}</option>)}
                  </select>
                </label>
              )}
              {widget.type === 'text' && <label className="hint" style={{ flex: 1 }}>text <textarea data-testid={`ed-prop-text-${id}`} rows={2} style={{ width: '100%' }} value={widget.text ?? ''} onChange={(e) => apply({ type: 'setPath', path: ['widgets', id, 'text'], value: e.target.value }, `prop:${id}:text`)} /></label>}
            </div>
          )
        })}
      </div>
    </>
  )
}

/** A report's own options (plan 32 DBR27): `strict` (an over-width block refuses the export), `limits` (pages, rows) and the cover default. A report is a run, so it has no refresh. */
function ReportSettings({ doc, diagnostics, apply }: { doc: ViewsDoc; diagnostics: ClientDiagnostic[]; apply: Apply }) {
  const own = diagnosticsFor(diagnostics, { area: 'document' }).filter((d) => /^(strict|limits|cover|refresh)/.test(d.path))
  const limits = (doc.limits ?? {}) as { pages?: number; rows?: number }
  const limit = (key: 'pages' | 'rows', text: string) => {
    if (text === '' && limits[key] === undefined) return
    if (text !== '' && !Number.isFinite(Number(text))) return
    apply({ type: 'setReportOption', key: `limits.${key}`, value: text === '' ? undefined : Number(text) }, `report:limits.${key}`)
  }
  return (
    <div className="card" style={{ marginBottom: 12 }} data-testid="ed-report-settings">
      <strong>Report options</strong>{' '}
      <label className="hint"><input type="checkbox" data-testid="ed-report-strict" checked={doc.strict === true} onChange={(e) => apply({ type: 'setReportOption', key: 'strict', value: e.target.checked ? true : undefined })} /> strict: a block wider than the page refuses the export</label>{' '}
      <label className="hint">page limit <input type="number" data-testid="ed-report-limit-pages" style={{ width: 70 }} value={limits.pages ?? ''} onChange={(e) => limit('pages', e.target.value)} /></label>{' '}
      <label className="hint">row limit <input type="number" data-testid="ed-report-limit-rows" style={{ width: 80 }} value={limits.rows ?? ''} onChange={(e) => limit('rows', e.target.value)} /></label>{' '}
      <label className="hint"><input type="checkbox" data-testid="ed-report-cover" checked={(doc.cover as { parameters?: boolean } | undefined)?.parameters === true} onChange={(e) => apply({ type: 'setReportOption', key: 'cover.parameters', value: e.target.checked ? true : undefined })} /> cover page with the parameters, unless a layout says otherwise</label>
      <Problems diagnostics={own} testId="ed-report-problems" />
    </div>
  )
}
