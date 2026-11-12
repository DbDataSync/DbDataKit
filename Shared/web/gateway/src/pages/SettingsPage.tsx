import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, Link, useIsAdmin } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import { GatewayShell } from '../GatewayShell'
import { MODULES, SEMANTIC_NUMBERS, cacheSeconds, editFor, groupDiagnostics, placeOf, restartText, saveState, semanticNumber, shown, toggleModule, type FieldKey, type SemanticNumber } from '../settings'
import type { Diagnostic, SettingsCheck, SettingsWritten } from '../types'

export interface SettingsViewProps {
  loading: boolean
  loadError: string | null
  text: string
  dirty: boolean
  masked: boolean
  tab: 'form' | 'yaml'
  check: SettingsCheck | undefined
  checking: boolean
  checkFailed: boolean
  allowed: boolean
  busy: boolean
  note: string
  /** What the last save did, or null. */
  saved: { written: SettingsWritten; text: string } | null
  /** The Gateway's refusal of the last save in its words (and its diagnostics), or null. */
  refusal: { message: string; diagnostics: Diagnostic[] } | null
  /** The file changed since it was read. */
  stale: boolean
  cacheText: string | null
  /** What is typed in a semantic number field that is not a valid number yet, by field. */
  semanticText?: Record<string, string>
  onSemanticText?: (field: string, text: string) => void
  onTab: (tab: 'form' | 'yaml') => void
  onText: (text: string) => void
  onField: (field: FieldKey, value: string | number | boolean | string[]) => void
  onCacheText: (text: string) => void
  onNote: (note: string) => void
  onSave: () => void
  onReload: () => void
}

/** One whole-number field of the `semantic:` section: what is typed is kept (by the page) until it is a number in range, and the Gateway is asked only then. Stateless, as the other fields are. */
function SemanticNumberField({ field, value, typed, disabled, items, onField, onTyped }: { field: SemanticNumber; value: number; typed: string | undefined; disabled: boolean; items: Diagnostic[] | undefined; onField: (field: FieldKey, value: number) => void; onTyped: (key: string, text: string) => void }) {
  const problem = typed !== undefined && semanticNumber(field, typed) === null ? `A whole number from ${field.min} to ${field.max}.` : null
  const id = `settings-${field.key.replace(/\./g, '-')}`
  return (
    <div className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'center', marginBottom: 8 }}>
      <label htmlFor={id}>{field.label}</label>
      <input id={id} className="input" style={{ width: 130 }} inputMode="numeric" disabled={disabled} value={typed ?? String(value)} aria-invalid={problem ? true : undefined} data-testid={id}
        onChange={(e) => { onTyped(field.key, e.target.value); const n = semanticNumber(field, e.target.value); if (n !== null) onField(field.key, n) }} />
      <span className="hint">{field.hint}</span>
      {problem && <p className="hint" role="alert" data-testid={`${id}-problem`}>{problem}</p>}
      <FieldErrors field={field.key} items={items} />
    </div>
  )
}

function FieldErrors({ field, items }: { field: FieldKey; items: Diagnostic[] | undefined }) {
  if (!items || items.length === 0) return null
  return <>{items.map((d, i) => <p key={i} className="hint" role="alert" data-severity={d.severity} data-testid={`settings-error-${field}`}>{d.message}{d.fix ? ` ${d.fix}` : ''} <span className="mono">({placeOf(d)})</span></p>)}</>
}

/** The Gateway's serving settings, `server.yaml` (plan 33, UE17): a form for the fields people change and the text itself; one document, checked by the Gateway's own validator as it changes, saved as a commit of the configuration. Draws what it is given. */
export function SettingsView(p: SettingsViewProps) {
  const grouped = groupDiagnostics(p.check?.diagnostics ?? [])
  const values = shown(p.check?.fields)
  const save = saveState({ allowed: p.allowed, dirty: p.dirty, checking: p.checking, check: p.check, checkFailed: p.checkFailed, busy: p.busy })
  const cacheProblem = p.cacheText !== null && cacheSeconds(p.cacheText) === null ? 'A whole number of seconds, 0 to 3600 (0 turns the cache off).' : null
  const restart = p.check ? restartText(p.check.restart_keys, false) : null
  return (
    <div className="pane" data-testid="gateway-settings">
      <div className="page-head">
        <h1 className="page-title">Server settings</h1>
        <span className="page-note">server.yaml: the modules this Gateway serves and how. A save is a commit of the configuration; what a running Gateway reads only when it starts is said before you save.</span>
      </div>
      {p.loading && <div className="empty" role="status" data-testid="settings-loading">Reading server.yaml…</div>}
      {p.loadError && <div className="empty" role="alert" data-testid="settings-load-error">server.yaml could not be read: {p.loadError}</div>}
      {!p.loading && !p.loadError && (
        <>
          {p.stale && (
            <div className="empty" role="alert" data-testid="settings-conflict">
              server.yaml changed since you read it (someone else saved it, or it was edited by hand). Your draft is kept below; read the file again to see what changed. <button type="button" className="btn btn-sm" onClick={p.onReload} data-testid="settings-reload">Read it again and drop my draft</button>
            </div>
          )}
          <div className="row" style={{ gap: 8, marginBottom: 8 }} role="tablist">
            <button type="button" role="tab" aria-selected={p.tab === 'form'} className={p.tab === 'form' ? 'btn btn-primary' : 'btn'} onClick={() => p.onTab('form')} data-testid="settings-tab-form">Form</button>
            <button type="button" role="tab" aria-selected={p.tab === 'yaml'} className={p.tab === 'yaml' ? 'btn btn-primary' : 'btn'} onClick={() => p.onTab('yaml')} data-testid="settings-tab-yaml">server.yaml</button>
          </div>
          {p.masked && <p className="hint" data-testid="settings-masked-note">A password or key in the file is shown as '********'. Leave it as it is to keep the value; type over it to replace it. The value is never sent here.</p>}

          {p.tab === 'form' && (
            <div className="card" data-testid="settings-form">
              <div className="card-body">
                <fieldset style={{ border: 0, padding: 0, margin: '0 0 12px' }} data-testid="settings-modules">
                  <legend className="card-title">Modules this Gateway serves</legend>
                  {MODULES.map((m) => (
                    <label key={m} className="row" style={{ gap: 6 }}>
                      <input type="checkbox" checked={values.modules.includes(m)} disabled={!p.allowed || !p.check} onChange={(e) => p.onField('modules', toggleModule(values.modules, m, e.target.checked))} data-testid={`settings-module-${m}`} /> {m}
                    </label>
                  ))}
                  <FieldErrors field="modules" items={grouped.byField.modules} />
                </fieldset>
                <div className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'center', marginBottom: 8 }}>
                  <label htmlFor="settings-cache-seconds">Views cache (seconds)</label>
                  <input id="settings-cache-seconds" className="input" style={{ width: 90 }} inputMode="numeric" disabled={!p.allowed || !p.check} value={p.cacheText ?? String(values.cacheSeconds)} aria-invalid={cacheProblem ? true : undefined}
                    onChange={(e) => { p.onCacheText(e.target.value); const n = cacheSeconds(e.target.value); if (n !== null) p.onField('views.cacheSeconds', n) }} data-testid="settings-cache-seconds" />
                  <span className="hint">How long the rows of a request are kept for the person who asked; 0 turns the cache off.</span>
                </div>
                {cacheProblem && <p className="hint" role="alert" data-testid="settings-cache-problem">{cacheProblem}</p>}
                <FieldErrors field="views.cacheSeconds" items={grouped.byField['views.cacheSeconds']} />
                <div className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'center', marginBottom: 8 }}>
                  <label htmlFor="settings-export-mode">PDF export of views</label>
                  <select id="settings-export-mode" className="input" disabled={!p.allowed || !p.check} value={values.exportMode} onChange={(e) => p.onField('views.export.mode', e.target.value)} data-testid="settings-export-mode">
                    <option value="">off</option><option value="pinned">pinned browser</option><option value="system">installed browser</option>
                  </select>
                  <input className="input" aria-label="Browser path" placeholder="browser path (installed browser)" disabled={!p.allowed || !p.check || values.exportMode !== 'system'} value={values.exportPath} onChange={(e) => p.onField('views.export.path', e.target.value)} data-testid="settings-export-path" />
                </div>
                <FieldErrors field="views.export.mode" items={grouped.byField['views.export.mode']} />
                <FieldErrors field="views.export.path" items={grouped.byField['views.export.path']} />
                <fieldset style={{ border: 0, padding: 0, margin: '12px 0' }} data-testid="settings-semantic">
                  <legend className="card-title">Semantic models</legend>
                  {SEMANTIC_NUMBERS.map((f) => <SemanticNumberField key={f.key} field={f} value={values.semantic[f.key]} disabled={!p.allowed || !p.check} items={grouped.byField[f.key]} typed={p.semanticText?.[f.key]} onField={p.onField} onTyped={(k, t) => p.onSemanticText?.(k, t)} />)}
                  <p className="hint" data-testid="settings-semantic-text">The models set up by hand (catalog file and the data source of each source) and the Analysis Services connections (a secret reference, never a password) are edited in the server.yaml tab, under <span className="mono">semantic:</span>.</p>
                </fieldset>
                <fieldset style={{ border: 0, padding: 0, margin: '12px 0' }} data-testid="settings-telemetry">
                  <legend className="card-title">Telemetry (OpenTelemetry)</legend>
                  <label className="row" style={{ gap: 6 }}>
                    <input type="checkbox" checked={values.telemetryEnabled} disabled={!p.allowed || !p.check} onChange={(e) => p.onField('telemetry.enabled', e.target.checked)} data-testid="settings-telemetry-enabled" /> Send traces and metrics
                  </label>
                  <FieldErrors field="telemetry.enabled" items={grouped.byField['telemetry.enabled']} />
                  <div className="row" style={{ gap: 10, flexWrap: 'wrap', alignItems: 'center', margin: '6px 0' }}>
                    <label htmlFor="settings-telemetry-endpoint">Collector address</label>
                    <input id="settings-telemetry-endpoint" className="input" placeholder="http://collector.internal:4317" disabled={!p.allowed || !p.check || !values.telemetryEnabled} value={values.telemetryEndpoint} onChange={(e) => p.onField('telemetry.endpoint', e.target.value)} data-testid="settings-telemetry-endpoint" />
                    <label htmlFor="settings-telemetry-protocol">Protocol</label>
                    <select id="settings-telemetry-protocol" className="input" disabled={!p.allowed || !p.check || !values.telemetryEnabled} value={values.telemetryProtocol} onChange={(e) => p.onField('telemetry.protocol', e.target.value)} data-testid="settings-telemetry-protocol">
                      <option value="grpc">gRPC</option><option value="http">HTTP</option>
                    </select>
                  </div>
                  <FieldErrors field="telemetry.endpoint" items={grouped.byField['telemetry.endpoint']} />
                  <FieldErrors field="telemetry.protocol" items={grouped.byField['telemetry.protocol']} />
                  <p className="hint" data-testid="settings-telemetry-note">Off by default; read when the Gateway starts. The share of traces kept (<code>telemetry.samplingRatio</code>) and the instance name are edited in the server.yaml tab. A span or a metric never holds a secret, a query text or a value.</p>
                </fieldset>
                <label className="row" style={{ gap: 6 }}>
                  <input type="checkbox" checked={values.logSql} disabled={!p.allowed || !p.check} onChange={(e) => p.onField('logSql', e.target.checked)} data-testid="settings-log-sql" /> Log the SQL text of queries
                </label>
                <FieldErrors field="logSql" items={grouped.byField.logSql} />
              </div>
            </div>
          )}

          {p.tab === 'yaml' && (
            <textarea className="input mono" rows={22} style={{ width: '100%' }} spellCheck={false} readOnly={!p.allowed} value={p.text} onChange={(e) => p.onText(e.target.value)} data-testid="settings-yaml" />
          )}

          {(grouped.other.length > 0 || (p.check?.problems.length ?? 0) > 0) && (
            <div className="card flush" style={{ marginTop: 12 }} data-testid="settings-diagnostics">
              {(p.check?.problems ?? []).map((m, i) => <div key={`p${i}`} className="grid-row" role="alert" data-testid="settings-problem"><span>{m}</span></div>)}
              {grouped.other.map((d, i) => (
                <div key={i} className="grid-row" style={{ gridTemplateColumns: '0.8fr 0.7fr 4fr 1.2fr', gap: 14 }} data-testid="settings-diagnostic">
                  <span className="mono">{d.code}</span><span data-severity={d.severity}>{d.severity}</span><span>{d.message}{d.fix ? ` ${d.fix}` : ''}</span><span className="mono">{placeOf(d)}</span>
                </div>
              ))}
            </div>
          )}

          {restart && <p className="hint" role="status" data-testid="settings-restart-draft">{restart}</p>}
          <div className="row" style={{ gap: 10, marginTop: 12, alignItems: 'center', flexWrap: 'wrap' }}>
            <input className="input" aria-label="Why" placeholder="why (kept with the commit)" maxLength={200} value={p.note} onChange={(e) => p.onNote(e.target.value)} data-testid="settings-note" />
            <button type="button" className="btn btn-primary" disabled={!save.enabled} title={save.why ?? 'Check, write and commit server.yaml'} onClick={p.onSave} data-testid="settings-save">Save</button>
            {save.why && <span className="hint" data-testid="settings-save-why">{save.why}</span>}
          </div>
          {p.refusal && (
            <div className="empty" role="alert" data-testid="settings-refused">
              {p.refusal.message}
              {p.refusal.diagnostics.map((d, i) => <div key={i} className="mono" data-testid="settings-refused-diagnostic">{d.code} {d.message} ({placeOf(d)})</div>)}
            </div>
          )}
          {p.saved && (
            <div className="empty" role="status" data-testid="settings-saved">
              {p.saved.written.changed.length === 0 ? 'Nothing was different, so nothing was written.' : `Saved: ${p.saved.written.changed.join(', ')}.`}
              {p.saved.written.commit && <> <Link to={`/config/${p.saved.written.commit}`} data-testid="settings-saved-commit">See the change in the history ({p.saved.written.commit.slice(0, 7)})</Link>.</>}
              {p.saved.written.changed.length > 0 && <span data-testid="settings-saved-restart" data-restart={p.saved.written.restart_needed ? 'true' : 'false'}> {restartText(p.saved.written.restart_keys, true)}</span>}
            </div>
          )}
        </>
      )}
    </div>
  )
}

/** The settings screen: reads the file, keeps the draft (the one document of the form and the text), has the Gateway check it a moment after each change, and saves it against the version read. */
export function SettingsPage() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const file = useQuery({ queryKey: ['gateway', 'settings'], queryFn: gatewayApi.settings.read, enabled: isAdmin, retry: false, refetchOnWindowFocus: false })
  const [draft, setDraft] = useState<string | null>(null)
  const [tab, setTab] = useState<'form' | 'yaml'>('form')
  const [note, setNote] = useState('')
  const [cacheText, setCacheText] = useState<string | null>(null)
  const [semanticText, setSemanticText] = useState<Record<string, string>>({})
  const [saved, setSaved] = useState<SettingsViewProps['saved']>(null)
  const [refusal, setRefusal] = useState<SettingsViewProps['refusal']>(null)
  const [stale, setStale] = useState(false)
  const text = draft ?? file.data?.text ?? ''
  const dirty = file.data !== undefined && text !== file.data.text

  // the Gateway checks the draft a moment after the last change (its own validator: no second one here)
  const [settled, setSettled] = useState(text)
  useEffect(() => { const t = setTimeout(() => setSettled(text), 350); return () => clearTimeout(t) }, [text])
  const check = useQuery({ queryKey: ['gateway', 'settings', 'check', settled], queryFn: () => gatewayApi.settings.check(settled), enabled: isAdmin && file.data !== undefined, retry: false, refetchOnWindowFocus: false, placeholderData: (previous) => previous })
  const checking = settled !== text || check.isFetching

  const edit = useMutation({ mutationFn: (change: ReturnType<typeof editFor>) => gatewayApi.settings.edit(text, change), onSuccess: (next) => { setDraft(next); setSaved(null) } })
  const write = useMutation({
    mutationFn: () => gatewayApi.settings.write(text, file.data?.version ?? null, note),
    onSuccess: async (written) => {
      setSaved({ written, text })
      setRefusal(null); setStale(false); setDraft(null); setNote(''); setCacheText(null); setSemanticText({})
      await queryClient.invalidateQueries({ queryKey: ['gateway', 'settings'] })
      await queryClient.invalidateQueries({ queryKey: ['gateway', 'history'] })
    },
    onError: (error: unknown) => {
      setSaved(null)
      const api = error as ApiError
      if (api.status === 409) { setStale(true); setRefusal(null); return }
      const body = api.body as { detail?: { diagnostics?: Diagnostic[] }; problems?: string[] } | undefined
      setRefusal({ message: error instanceof Error ? error.message : String(error), diagnostics: body?.detail?.diagnostics ?? [] })
    },
  })

  if (!isAdmin) return <GatewayShell crumbs={[{ label: 'Server settings', heading: true }]}><div className="pane"><div className="empty">This screen is for administrators.</div></div></GatewayShell>
  return (
    <GatewayShell crumbs={[{ label: 'Configuration history', to: '/config' }, { label: 'Server settings', heading: true }]}>
      <SettingsView
        loading={file.isPending} loadError={file.error ? (file.error instanceof Error ? file.error.message : String(file.error)) : null}
        text={text} dirty={dirty} masked={file.data?.masked ?? false} tab={tab}
        check={check.data} checking={checking} checkFailed={check.isError && settled === text} allowed={isAdmin} busy={write.isPending} note={note}
        saved={saved} refusal={refusal} stale={stale} cacheText={cacheText} semanticText={semanticText} onSemanticText={(k, t) => setSemanticText((m) => ({ ...m, [k]: t }))}
        onTab={setTab}
        onText={(t) => { setDraft(t); setSaved(null); setCacheText(null); setSemanticText({}) }}
        onField={(field, value) => edit.mutate(editFor(field, value))}
        onCacheText={setCacheText}
        onNote={setNote}
        onSave={() => write.mutate()}
        onReload={() => { setDraft(null); setStale(false); setRefusal(null); setCacheText(null); void queryClient.invalidateQueries({ queryKey: ['gateway', 'settings'] }) }}
      />
    </GatewayShell>
  )
}
