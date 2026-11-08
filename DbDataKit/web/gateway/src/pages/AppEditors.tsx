import type { ReactNode } from 'react'
import { bindingRows, instanceFormProblem, KNOWN_SETTINGS, type InstanceForm } from '../appEditors'
import { NEEDS_PUBLISHER } from '../apps'
import type { AppsCaller, AppSummary } from '../types'

// The editors of the Apps screen (plan 33): the instance form (UE01), the publish panel (UE02) and the navigation set editor (UE03). Each is a view over its props, as the lists in AppsPage are, so it is tested without its calls.
// The roles are the Gateway's: a button that is off says why, and the Gateway refuses the call again if the screen is wrong.

/** The grid rows clip long text; what a person must read (a reason, an address, an id) wraps instead. */
export const wrap = { whiteSpace: 'normal', overflowWrap: 'anywhere' } as const

/** A button that is off says why, next to itself, so a person is not left guessing which role or step is missing. */
export function Why({ reason, testId }: { reason: string | null; testId: string }): ReactNode {
  return reason ? <div className="hint" style={wrap} data-testid={testId}>{reason}</div> : null
}

// ---- UE01: the instance form ----

export interface InstanceEditorProps {
  form: InstanceForm
  isNew: boolean
  apps: AppSummary[]
  /** The versions of each app, for the pin chooser. */
  versionsOf: (app: string) => number[]
  /** The models the version the instance would run uses; null while they are being read (saving waits, so no binding is dropped by mistake). */
  models: string[] | null
  /** The names of the instances that exist, so a new name is not one of them. */
  existing: string[]
  caller: AppsCaller
  busy: boolean
  onChange: (form: InstanceForm) => void
  onSave: () => void
  onCancel: () => void
}

/** Why the instance form cannot be saved, or null when the call may be tried (the Gateway still decides). */
export function instanceSaveBlock(p: Pick<InstanceEditorProps, 'form' | 'isNew' | 'existing' | 'caller' | 'models'>): string | null {
  if (!p.caller.can_publish) return NEEDS_PUBLISHER
  const problem = instanceFormProblem(p.form, p.isNew, p.existing)
  if (problem) return problem
  if (p.models === null) return "Reading what this version of the app uses; saving waits so that no binding is dropped by mistake."
  return null
}

/** Make an instance or change one: the app and version it runs, its title, its settings (the host reads two, an app may read its own) and, for each Store model the app uses, the model instance it is bound to. */
export function InstanceEditorView(p: InstanceEditorProps) {
  const { form } = p
  const set = (patch: Partial<InstanceForm>) => p.onChange({ ...form, ...patch })
  const reason = instanceSaveBlock(p)
  const rows = p.models === null ? [] : bindingRows(p.models, form.bindings)
  const known = KNOWN_SETTINGS.find((k) => form.settings.some((s) => s.key.trim() === k.key))
  return (
    <div className="card" data-testid="instance-editor" data-mode={p.isNew ? 'new' : 'edit'}>
      <div className="card-head"><span className="card-title">{p.isNew ? 'New instance' : `Instance ${form.name}`}</span></div>
      <div className="card-body">
        <div className="row" style={{ gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          <label style={{ display: 'grid', gap: 2 }}>
            <span className="hint">Name (its address is /apps/&lt;name&gt;/)</span>
            <input className="input sm" value={form.name} disabled={!p.isNew || !p.caller.can_publish} onChange={(e) => set({ name: e.target.value })} data-testid="instance-editor-name" aria-label="Instance name" />
          </label>
          <label style={{ display: 'grid', gap: 2 }}>
            <span className="hint">App</span>
            <select className="input sm" value={form.app} disabled={!p.isNew || !p.caller.can_publish} onChange={(e) => set({ app: e.target.value, pin: '', bindings: {} })} data-testid="instance-editor-app" aria-label="App">
              {p.isNew && <option value="">Choose an app</option>}
              {p.apps.map((a) => <option key={a.name} value={a.name}>{a.name}</option>)}
            </select>
          </label>
          <label style={{ display: 'grid', gap: 2 }}>
            <span className="hint">Version</span>
            <select className="input sm" value={form.pin} disabled={form.app === '' || !p.caller.can_publish} onChange={(e) => set({ pin: e.target.value })} data-testid="instance-editor-pin" aria-label="Version">
              <option value="">Follow the app</option>
              {p.versionsOf(form.app).map((v) => <option key={v} value={String(v)}>Pin to version {v}</option>)}
            </select>
          </label>
          <label style={{ display: 'grid', gap: 2 }}>
            <span className="hint">Title (shown in menus; optional)</span>
            <input className="input sm" value={form.title} disabled={!p.caller.can_publish} onChange={(e) => set({ title: e.target.value })} data-testid="instance-editor-title" aria-label="Title" />
          </label>
        </div>

        <h3 className="hint" style={{ margin: '16px 0 4px' }}>Settings</h3>
        <datalist id="instance-editor-known-settings">{KNOWN_SETTINGS.map((k) => <option key={k.key} value={k.key} />)}</datalist>
        {form.settings.length === 0 && <div className="hint" data-testid="instance-editor-settings-none">No setting. The app and the host use their defaults.</div>}
        {form.settings.map((s, i) => (
          <div key={i} className="row" style={{ gap: 6, marginBottom: 4 }} data-testid={`instance-editor-setting-${i}`}>
            <input className="input sm" list="instance-editor-known-settings" style={{ width: 220, flex: 'none' }} placeholder="name" value={s.key} disabled={!p.caller.can_publish}
              onChange={(e) => set({ settings: form.settings.map((x, j) => j === i ? { ...x, key: e.target.value } : x) })} data-testid={`instance-editor-setting-key-${i}`} aria-label={`Setting ${i + 1} name`} />
            <input className="input sm" placeholder="value" value={s.value} disabled={!p.caller.can_publish}
              onChange={(e) => set({ settings: form.settings.map((x, j) => j === i ? { ...x, value: e.target.value } : x) })} data-testid={`instance-editor-setting-value-${i}`} aria-label={`Setting ${i + 1} value`} />
            <button type="button" className="btn sm" disabled={!p.caller.can_publish} onClick={() => set({ settings: form.settings.filter((_, j) => j !== i) })} data-testid={`instance-editor-setting-remove-${i}`}>Remove</button>
          </div>
        ))}
        {known && <div className="hint" style={wrap} data-testid="instance-editor-setting-hint">{known.key}: {known.hint}</div>}
        <button type="button" className="btn sm" disabled={!p.caller.can_publish} onClick={() => set({ settings: [...form.settings, { key: '', value: '' }] })} data-testid="instance-editor-setting-add">Add a setting</button>

        <h3 className="hint" style={{ margin: '16px 0 4px' }}>Data</h3>
        <p className="hint" style={{ margin: '0 0 6px' }}>Each Store model the app uses is bound to one of that model's instances. Left empty, the model is not bound and the app reads none of it.</p>
        {form.app === '' && <div className="hint" data-testid="instance-editor-bindings-wait">Choose an app to see the models it uses.</div>}
        {form.app !== '' && p.models === null && <div className="hint" data-testid="instance-editor-bindings-loading">Reading what this version of the app uses…</div>}
        {p.models !== null && rows.length === 0 && <div className="hint" data-testid="instance-editor-bindings-none">This version of {form.app} uses no Store model, so there is nothing to bind.</div>}
        {rows.map((r) => (
          <div key={r.model} className="row" style={{ gap: 6, marginBottom: 4, alignItems: 'center' }} data-testid={`instance-editor-binding-row-${r.model}`}>
            <span className="mono" style={{ width: 220, flex: 'none' }}>{r.model}</span>
            <span aria-hidden>→</span>
            <input className="input sm" placeholder="model instance, for example default" value={form.bindings[r.model] ?? ''} disabled={!p.caller.can_publish}
              onChange={(e) => set({ bindings: { ...form.bindings, [r.model]: e.target.value } })} data-testid={`instance-editor-binding-${r.model}`} aria-label={`Instance of ${r.model}`} />
            {!r.used && <span className="hint" style={wrap} data-testid={`instance-editor-binding-stale-${r.model}`}>This version does not use {r.model}; saving removes this binding.</span>}
          </div>
        ))}

        <div className="row" style={{ gap: 8, marginTop: 14 }}>
          <button type="button" className="btn btn-primary" disabled={reason !== null || p.busy} onClick={p.onSave} data-testid="instance-editor-save">{p.isNew ? 'Make the instance' : 'Save the instance'}</button>
          <button type="button" className="btn" onClick={p.onCancel} data-testid="instance-editor-cancel">Cancel</button>
        </div>
        <Why reason={reason} testId="instance-editor-why" />
      </div>
    </div>
  )
}
