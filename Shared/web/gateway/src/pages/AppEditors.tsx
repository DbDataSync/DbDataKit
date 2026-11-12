import { useState, type ReactNode } from 'react'
import { availableMembers, bindingRows, egressChoices, emptyAddress, instanceFormProblem, isRename, KNOWN_SETTINGS, moveEntry, navFormProblem, packageApproveBlock, publishBlock, sizeLabel, type InstanceForm, type NavForm, type Prepared } from '../appEditors'
import { NEEDS_PUBLISHER, shortHash } from '../apps'
import type { AppCheck, AppDomainCheck, AppsCaller, AppSummary } from '../types'

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
  /** Asks the Gateway whether a custom domain is proved (its DNS challenge record, or the name already resolving here) and for the record to create; absent where the screen cannot ask. */
  verifyAddress?: (host: string) => Promise<AppDomainCheck>
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

/**
 * The Verify button of a custom domain and what it said (AP08): accepted, and by which proof, or why not, with the TXT record to create (name and value, to copy). What it said belongs to the name asked: an edited name is a new question.
 * It has its own state, so the form above stays a view over its props.
 */
export function AddressVerify({ index, host, canPublish, verify }: { index: number; host: string; canPublish: boolean; verify: (host: string) => Promise<AppDomainCheck> }) {
  const [result, setResult] = useState<{ host: string; check: AppDomainCheck | string } | null>(null)
  const [asking, setAsking] = useState(false)
  const ask = () => {
    const named = host.trim()
    setAsking(true)
    verify(named).then((check) => setResult({ host, check }), (e) => setResult({ host, check: e instanceof Error ? e.message : String(e) })).finally(() => setAsking(false))
  }
  const shown = result && result.host === host ? result.check : null
  return (
    <>
      <button type="button" className="btn sm" disabled={!canPublish || host.trim() === '' || asking} onClick={ask} data-testid={`instance-editor-address-verify-${index}`}>Verify</button>
      {shown !== null && <DomainCheckView index={index} check={shown} />}
    </>
  )
}

/** What Verify answered, as the screen draws it. */
export function DomainCheckView({ index, check }: { index: number; check: AppDomainCheck | string }) {
  if (typeof check === 'string') return <div className="hint" role="alert" style={{ flexBasis: '100%' }} data-testid={`instance-editor-address-verify-result-${index}`} data-verified="error">{check}</div>
  return (
    <div className="hint" style={{ flexBasis: '100%', overflowWrap: 'anywhere' }} data-testid={`instance-editor-address-verify-result-${index}`} data-verified={String(check.verified)}>
      {check.verified
        ? <span data-testid={`instance-editor-address-verified-${index}`}>{check.via === 'challenge' ? 'Verified: the TXT record holds the value.' : 'Verified: the name resolves to this host.'}</span>
        : <span data-testid={`instance-editor-address-unverified-${index}`}>{check.reason}</span>}
      <div>To prove the name is yours, create the DNS record <b>TXT</b> <code className="mono" data-testid={`instance-editor-address-challenge-name-${index}`}>{check.record.name}</code> with the value <code className="mono" data-testid={`instance-editor-address-challenge-value-${index}`}>{check.record.value}</code>, then Verify.</div>
    </div>
  )
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

        <h3 className="hint" style={{ margin: '16px 0 4px' }}>Addresses</h3>
        <p className="hint" style={{ margin: '0 0 6px' }}>Besides /apps/{form.name || '<name>'}/ the instance can be served at a name of its own: a subdomain of the host's apps domain ({form.name || '<name>'}.&lt;apps domain&gt;), or a custom domain with its certificate (a secret that holds the PFX). The Gateway checks that the name reaches it, or that you control it (a DNS TXT record that Verify shows), and that the certificate is for it before it accepts the address.</p>
        {form.addresses.length === 0 && <div className="hint" data-testid="instance-editor-addresses-none">No address: the instance is served at its path only.</div>}
        {form.addresses.map((a, i) => (
          <div key={i} className="row" style={{ gap: 6, marginBottom: 4, flexWrap: 'wrap', alignItems: 'center' }} data-testid={`instance-editor-address-${i}`}>
            <select className="input sm" style={{ width: 130, flex: 'none' }} value={a.kind} disabled={!p.caller.can_publish}
              onChange={(e) => set({ addresses: form.addresses.map((x, j) => j === i ? { ...x, kind: e.target.value as 'subdomain' | 'custom' } : x) })} data-testid={`instance-editor-address-kind-${i}`} aria-label={`Address ${i + 1} kind`}>
              <option value="subdomain">Subdomain</option>
              <option value="custom">Custom domain</option>
            </select>
            <input className="input sm" style={{ width: 240, flex: 'none' }} placeholder={a.kind === 'subdomain' ? `${form.name || 'name'}.apps.example.com` : 'reports.example.com'} value={a.host} disabled={!p.caller.can_publish}
              onChange={(e) => set({ addresses: form.addresses.map((x, j) => j === i ? { ...x, host: e.target.value } : x) })} data-testid={`instance-editor-address-host-${i}`} aria-label={`Address ${i + 1} name`} />
            <input className="input sm" style={{ width: 110, flex: 'none' }} placeholder="/" value={a.path} disabled={!p.caller.can_publish}
              onChange={(e) => set({ addresses: form.addresses.map((x, j) => j === i ? { ...x, path: e.target.value } : x) })} data-testid={`instance-editor-address-path-${i}`} aria-label={`Address ${i + 1} path`} />
            {a.kind === 'custom' && <>
              <input className="input sm" style={{ width: 220, flex: 'none' }} placeholder="certificate: secret:<name>" value={a.certificate} disabled={!p.caller.can_publish}
                onChange={(e) => set({ addresses: form.addresses.map((x, j) => j === i ? { ...x, certificate: e.target.value } : x) })} data-testid={`instance-editor-address-certificate-${i}`} aria-label={`Address ${i + 1} certificate secret`} />
              <input className="input sm" style={{ width: 200, flex: 'none' }} placeholder="password: secret:<name> (if any)" value={a.certificatePassword} disabled={!p.caller.can_publish}
                onChange={(e) => set({ addresses: form.addresses.map((x, j) => j === i ? { ...x, certificatePassword: e.target.value } : x) })} data-testid={`instance-editor-address-password-${i}`} aria-label={`Address ${i + 1} certificate password secret`} />
            </>}
            <label className="row" style={{ gap: 4 }}>
              <input type="checkbox" checked={a.primary} disabled={!p.caller.can_publish}
                onChange={(e) => set({ addresses: form.addresses.map((x, j) => ({ ...x, primary: j === i ? e.target.checked : false })) })} data-testid={`instance-editor-address-primary-${i}`} />
              <span className="hint">Primary</span>
            </label>
            {a.kind === 'custom' && p.verifyAddress && <AddressVerify index={i} host={a.host} canPublish={p.caller.can_publish} verify={p.verifyAddress} />}
            <button type="button" className="btn sm" disabled={!p.caller.can_publish} onClick={() => set({ addresses: form.addresses.filter((_, j) => j !== i) })} data-testid={`instance-editor-address-remove-${i}`}>Remove</button>
          </div>
        ))}
        <button type="button" className="btn sm" disabled={!p.caller.can_publish} onClick={() => set({ addresses: [...form.addresses, { ...emptyAddress(), kind: 'subdomain', host: form.name ? `${form.name}.` : '' }] })} data-testid="instance-editor-address-add">Add an address</button>

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

// ---- UE02: publish a version, and approve exact files with the origins chosen ----

export interface PublishViewProps {
  caller: AppsCaller
  mode: 'folder' | 'manifest'
  /** What the chosen folder (or the pasted text) became, or why it cannot be sent; null before anything is chosen. */
  prepared: Prepared | null
  pasted: string
  note: string
  /** The Gateway's answer about these files, null until it has one. */
  check: AppCheck | null
  checking: boolean
  /** Why the check call itself was refused, in the Gateway's words. */
  checkFailure: string | null
  /** The Gateway's refusal of the last publish or approval, in its words. */
  refusal: string | null
  busy: boolean
  by: string
  approveNote: string
  /** The origins ticked, as `directive=origin`. */
  chosenEgress: string[]
  onMode: (mode: 'folder' | 'manifest') => void
  onPickFiles: (files: FileList | null) => void
  onPaste: (text: string) => void
  onNote: (text: string) => void
  onPublish: () => void
  onBy: (text: string) => void
  onApproveNote: (text: string) => void
  onToggleEgress: (id: string) => void
  onApprove: () => void
  onClear: () => void
}

/** The Gateway's findings about files, one line each as the command line prints them. */
function Findings({ check }: { check: AppCheck }) {
  if (check.diagnostics.length === 0) return <div className="hint" data-testid="publish-diagnostics-none">The Gateway found nothing to fix.</div>
  return (
    <ul data-testid="publish-diagnostics" style={{ margin: '4px 0', paddingLeft: 18 }}>
      {check.diagnostics.map((d, i) => (
        <li key={i} className="mono" style={wrap} data-severity={d.severity} data-testid="publish-diagnostic">
          {d.file ? `${d.file}${d.line > 0 ? `:${d.line}` : ''}: ` : ''}{d.severity} {d.code}: {d.message}{d.fix ? ` (${d.fix})` : ''}
        </li>
      ))}
    </ul>
  )
}

/**
 * Publish a version of an app: the built folder (a directory input; every file is sent as base64 with the manifest as text, the package `apps publish --dir` makes), or a manifest alone. The Gateway checks the files first and says
 * what a publish would do (the version it would be, the trust, the hash); a refusal of the publish is shown as the Gateway says it. A trusted app is published only with an approval of exactly these files, which an approver makes
 * here, choosing which of the outside origins the manifest asks for are allowed; the approver is a person of the identity home with the Approver role, not the person publishing.
 */
export function PublishView(p: PublishViewProps) {
  const { check } = p
  const reason = publishBlock(p.caller, p.prepared, check, p.checking)
  const approveReason = packageApproveBlock(p.caller, check, p.by)
  const needsApproval = check !== null && check.trust === 'trusted' && !check.approval_id
  const choices = egressChoices(check?.about)
  return (
    <div className="card" data-testid="publish-panel">
      <div className="card-body">
        <div className="row" style={{ gap: 6, marginBottom: 8 }}>
          <button type="button" className={p.mode === 'folder' ? 'btn sm btn-primary' : 'btn sm'} onClick={() => p.onMode('folder')} data-testid="publish-mode-folder">A built folder</button>
          <button type="button" className={p.mode === 'manifest' ? 'btn sm btn-primary' : 'btn sm'} onClick={() => p.onMode('manifest')} data-testid="publish-mode-manifest">A manifest only</button>
        </div>
        {p.mode === 'folder'
          ? (
            <label style={{ display: 'grid', gap: 2 }}>
              <span className="hint">The folder that holds app.yaml and the files it serves (its build output; every file in it is sent, as <span className="mono">apps publish --dir</span> sends them)</span>
              {/* a directory input: the browser gives every file with its path inside the folder */}
              <input type="file" multiple {...{ webkitdirectory: '' }} disabled={!p.caller.can_publish} onChange={(e) => p.onPickFiles(e.target.files)} data-testid="publish-folder" aria-label="App folder" />
            </label>
          )
          : (
            <label style={{ display: 'grid', gap: 2 }}>
              <span className="hint">The text of app.yaml. For an app that serves pages, choose its folder instead; a manifest alone suits a portal of mounts and home views.</span>
              <textarea className="input" rows={8} value={p.pasted} disabled={!p.caller.can_publish} onChange={(e) => p.onPaste(e.target.value)} data-testid="publish-manifest" aria-label="Manifest" style={{ fontFamily: 'var(--mono, monospace)' }} />
            </label>
          )}
        {p.prepared?.ok === false && <div className="hint" role="alert" style={wrap} data-testid="publish-problem">{p.prepared.problem}</div>}
        {p.prepared?.ok === true && (
          <div style={{ marginTop: 8 }} data-testid="publish-summary">
            <span className="mono">{p.prepared.package.source}</span>: app.yaml and {p.prepared.package.fileCount} file{p.prepared.package.fileCount === 1 ? '' : 's'}, {sizeLabel(p.prepared.package.bytes)}
            {' '}<button type="button" className="btn sm" onClick={p.onClear} data-testid="publish-clear">Choose again</button>
          </div>
        )}
        {p.checking && <div className="hint" data-testid="publish-checking">Checking these files with the Gateway…</div>}
        {p.checkFailure && <div className="banner error" role="alert" data-testid="publish-check-failure"><span className="mark">!</span><span>{p.checkFailure}</span></div>}
        {check && (
          <div style={{ marginTop: 8 }} data-testid="publish-check" data-ok={check.ok ? 'true' : 'false'}>
            {check.name && <div data-testid="publish-would"><b>{check.name} {check.manifest_version}</b> would be version {check.next_version}, {check.trust === 'trusted' ? 'asking to be trusted' : 'sandboxed'}; hash <span className="mono" title={check.hash ?? undefined}>{shortHash(check.hash ?? '')}</span>.</div>}
            <Findings check={check} />
          </div>
        )}

        <div className="row" style={{ gap: 8, marginTop: 10, flexWrap: 'wrap' }}>
          <input className="input" style={{ maxWidth: 360 }} placeholder="note for this version (optional)" value={p.note} disabled={!p.caller.can_publish} onChange={(e) => p.onNote(e.target.value)} data-testid="publish-note" aria-label="Note" />
          <button type="button" className="btn btn-primary" disabled={reason !== null || p.busy} onClick={p.onPublish} data-testid="publish-send">{check?.name ? `Publish ${check.name} as version ${check.next_version}` : 'Publish'}</button>
        </div>
        <Why reason={reason} testId="publish-why" />
        {p.refusal && <div className="banner error" role="alert" style={{ marginTop: 8 }} data-testid="publish-refusal"><span className="mark">!</span><span style={wrap}>{p.refusal}</span></div>}

        {needsApproval && (
          <div style={{ marginTop: 14, borderTop: '1px solid var(--line, #ddd)', paddingTop: 10 }} data-testid="publish-approve">
            <h3 className="hint" style={{ margin: '0 0 4px' }}>Approve these exact files</h3>
            <p className="hint" style={{ margin: '0 0 6px', ...wrap }}>An approval is for the hash above: a changed byte needs a new one. The approver is named in the audit; the Gateway checks that they hold the Approver role and are not {p.caller.you || 'the person publishing'}.</p>
            {choices.length === 0
              ? <div className="hint" data-testid="publish-egress-none">The manifest asks for no outside address, so the approval allows none.</div>
              : (
                <fieldset style={{ border: 0, padding: 0, margin: '0 0 6px' }} data-testid="publish-egress">
                  <legend className="hint">Outside addresses the manifest asks for: tick the ones the app may use (none ticked, none allowed)</legend>
                  {choices.map((c) => (
                    <label key={c.id} style={{ display: 'block' }}>
                      <input type="checkbox" checked={p.chosenEgress.includes(c.id)} onChange={() => p.onToggleEgress(c.id)} data-testid={`publish-egress-${c.id}`} /> <span className="mono">{c.directive} {c.origin}</span>
                    </label>
                  ))}
                </fieldset>
              )}
            <div className="row" style={{ gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
              <input className="input sm" style={{ width: 220, flex: 'none' }} placeholder="approver" value={p.by} onChange={(e) => p.onBy(e.target.value)} data-testid="publish-approve-by" aria-label="Approver" />
              <input className="input" style={{ maxWidth: 320 }} placeholder="note (a link to the review, for example)" value={p.approveNote} onChange={(e) => p.onApproveNote(e.target.value)} data-testid="publish-approve-note" aria-label="Approval note" />
              <button type="button" className="btn btn-primary" disabled={approveReason !== null || p.busy} onClick={p.onApprove} data-testid="publish-approve-send">Approve these files</button>
            </div>
            <Why reason={approveReason} testId="publish-approve-why" />
          </div>
        )}
      </div>
    </div>
  )
}

// ---- UE03: the navigation set editor ----

export interface NavigationEditorProps {
  form: NavForm
  /** The names of the sets that exist (a new set may not take one of them). */
  existing: string[]
  /** The instances that exist, the members a set can list. */
  instances: { name: string; title: string | null }[]
  caller: AppsCaller
  busy: boolean
  onChange: (form: NavForm) => void
  onSave: () => void
  onCancel: () => void
}

/** Why the set cannot be saved, or null when the call may be tried (the Gateway still decides). */
export const navSaveBlock = (p: Pick<NavigationEditorProps, 'form' | 'existing' | 'caller'>): string | null =>
  !p.caller.can_publish ? NEEDS_PUBLISHER : navFormProblem(p.form, p.existing)

/** Make, reorder, rename and change a navigation set: a titled, ordered list of instances, each with the label the menu shows. A person sees only the members they hold a role on, so a member is never a promise. */
export function NavigationEditorView(p: NavigationEditorProps) {
  const { form } = p
  const set = (patch: Partial<NavForm>) => p.onChange({ ...form, ...patch })
  const reason = navSaveBlock(p)
  const addable = availableMembers(p.instances.map((i) => i.name), form.entries)
  const titleOf = (name: string) => p.instances.find((i) => i.name === name)?.title ?? null
  const off = !p.caller.can_publish
  return (
    <div className="card" data-testid="nav-editor" data-mode={form.original === null ? 'new' : 'edit'}>
      <div className="card-head"><span className="card-title">{form.original === null ? 'New navigation set' : `Navigation set ${form.original}`}</span></div>
      <div className="card-body">
        <div className="row" style={{ gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
          <label style={{ display: 'grid', gap: 2 }}>
            <span className="hint">Name</span>
            <input className="input sm" value={form.name} disabled={off} onChange={(e) => set({ name: e.target.value })} data-testid="nav-editor-name" aria-label="Set name" />
          </label>
          <label style={{ display: 'grid', gap: 2 }}>
            <span className="hint">Title (the menu's heading)</span>
            <input className="input sm" value={form.title} disabled={off} onChange={(e) => set({ title: e.target.value })} data-testid="nav-editor-title" aria-label="Set title" />
          </label>
        </div>
        {isRename(form) && <div className="hint" style={{ ...wrap, marginTop: 6 }} data-testid="nav-editor-rename">Saving makes the set under the name {form.name.trim() || '…'} and stops serving {form.original}, in one step.</div>}

        <h3 className="hint" style={{ margin: '16px 0 4px' }}>Members, in the order the menu shows them</h3>
        {form.entries.length === 0 && <div className="hint" data-testid="nav-editor-members-none">No member yet. Add an instance below.</div>}
        {form.entries.map((e, i) => (
          <div key={e.instance} className="row" style={{ gap: 6, marginBottom: 4, alignItems: 'center', flexWrap: 'wrap' }} data-testid={`nav-editor-member-${e.instance}`}>
            <span className="mono" style={{ width: 160, flex: 'none' }}>{e.instance}{titleOf(e.instance) && <div className="hint">{titleOf(e.instance)}</div>}</span>
            <input className="input sm" style={{ width: 180, flex: 'none' }} placeholder={`label (default: ${titleOf(e.instance) ?? e.instance})`} value={e.label} disabled={off}
              onChange={(ev) => set({ entries: form.entries.map((x, j) => j === i ? { ...x, label: ev.target.value } : x) })} data-testid={`nav-editor-label-${e.instance}`} aria-label={`Label of ${e.instance}`} />
            <input className="input sm" style={{ width: 120, flex: 'none' }} placeholder="icon" value={e.icon} disabled={off}
              onChange={(ev) => set({ entries: form.entries.map((x, j) => j === i ? { ...x, icon: ev.target.value } : x) })} data-testid={`nav-editor-icon-${e.instance}`} aria-label={`Icon of ${e.instance}`} />
            <button type="button" className="btn sm" disabled={off || i === 0} onClick={() => set({ entries: moveEntry(form.entries, i, -1) })} data-testid={`nav-editor-up-${e.instance}`} aria-label={`Move ${e.instance} up`}>Up</button>
            <button type="button" className="btn sm" disabled={off || i === form.entries.length - 1} onClick={() => set({ entries: moveEntry(form.entries, i, 1) })} data-testid={`nav-editor-down-${e.instance}`} aria-label={`Move ${e.instance} down`}>Down</button>
            <button type="button" className="btn sm" disabled={off} onClick={() => set({ entries: form.entries.filter((_, j) => j !== i) })} data-testid={`nav-editor-remove-${e.instance}`}>Remove</button>
          </div>
        ))}
        {p.instances.length === 0
          ? <div className="hint" data-testid="nav-editor-add-none">No instance exists yet: make one under Instances above, then list it here.</div>
          : addable.length === 0
            ? <div className="hint" data-testid="nav-editor-add-none">Every instance is already listed.</div>
            : (
              <select className="input sm" style={{ width: 240, marginTop: 6 }} value="" disabled={off} onChange={(e) => e.target.value !== '' && set({ entries: [...form.entries, { instance: e.target.value, label: '', icon: '' }] })} data-testid="nav-editor-add" aria-label="Add an instance">
                <option value="">Add an instance…</option>
                {addable.map((name) => <option key={name} value={name}>{name}</option>)}
              </select>
            )}

        <div className="row" style={{ gap: 8, marginTop: 14 }}>
          <button type="button" className="btn btn-primary" disabled={reason !== null || p.busy} onClick={p.onSave} data-testid="nav-editor-save">{form.original === null ? 'Make the set' : isRename(form) ? 'Save under the new name' : 'Save the set'}</button>
          <button type="button" className="btn" onClick={p.onCancel} data-testid="nav-editor-cancel">Cancel</button>
        </div>
        <Why reason={reason} testId="nav-editor-why" />
      </div>
    </div>
  )
}
