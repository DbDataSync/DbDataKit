import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CodeEditor, ErrorBanner, Field, useIsAdmin } from '@dbdatakit/shell'
import type { Diagnostic } from '../agents'
import {
  KIND_LABEL, NEW_CHOICES, isConnectionName, keptHere, outsideApi, outsideDetail, refusedDiagnostics, secretLabel, secretProblems, splitSummary, testSummary, webhookChoices, webhookYaml, yamlWebhookFormat,
  type OutsideConnection, type WebhookChoices, type WebhookSplit,
} from '../outsideConnections'

const columns = '1.6fr 1fr 3fr 1.4fr'

/**
 * The connections of outside systems the console keeps (plan 27a IX01): an API (kind `http`), a model provider (kind `model-provider`) and the delivery channels' (plan 22a GP18 fold) mail servers (kind `smtp`) and webhooks (`http` with a `webhook:` section), each a `connection/2` file with its address and the *references* of its secrets. A person edits the file as text with the loader's
 * diagnostics (line, code, fix), starts one from a template per kind, and types a secret once: it is kept in the console's secret store and the file holds only its reference. The list shows each connection's kind, address and sign-in, and says in words which secret cannot be read.
 */
export function OutsideConnections() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const list = useQuery({ queryKey: ['outside-connections'], queryFn: outsideApi.list })
  const [selected, setSelected] = useState<string | null>(null)
  const [yaml, setYaml] = useState('')
  const [problems, setProblems] = useState<Diagnostic[]>([])
  const [newName, setNewName] = useState('')
  const [newChoice, setNewChoice] = useState('http')
  const [pasted, setPasted] = useState('')
  const [marking, setMarking] = useState<{ choices: WebhookChoices; segments: number[]; keys: string[] } | null>(null)
  const [split, setSplit] = useState<WebhookSplit | null>(null)
  const choice = NEW_CHOICES.find((c) => c.id === newChoice) ?? NEW_CHOICES[0]
  const [secrets, setSecrets] = useState<Record<string, string>>({})
  const refresh = () => client.invalidateQueries({ queryKey: ['outside-connections'] })
  const connection: OutsideConnection | undefined = list.data?.connections.find((c) => c.name === selected)
  useEffect(() => { if (connection) { setYaml(connection.yaml); setProblems(connection.diagnostics) } }, [connection?.name, connection?.yaml]) // eslint-disable-line react-hooks/exhaustive-deps

  const save = useMutation({ mutationFn: () => outsideApi.save(selected!, yaml), onSuccess: (r) => { setProblems(r.diagnostics); refresh() } })
  const check = useMutation({ mutationFn: () => outsideApi.check(selected!, yaml), onSuccess: (r) => setProblems(r.diagnostics) })
  const remove = useMutation({ mutationFn: () => outsideApi.remove(selected!), onSuccess: () => { setSelected(null); refresh() } })
  const test = useMutation({ mutationFn: () => outsideApi.test(selected!) })
  const keep = useMutation({ mutationFn: (field: string) => outsideApi.setSecret(selected!, field, secrets[field] ?? '').then(() => field), onSuccess: (field) => { setSecrets((s) => ({ ...s, [field]: '' })); refresh() } })
  const start = useMutation({
    mutationFn: () => outsideApi.template(choice.kind, newName, choice.flavor),
    onSuccess: (r) => { setSelected(newName); setYaml(r.yaml); setProblems([]); setNewName(''); setSplit(null); setMarking(null) },
  })
  // a pasted webhook address: split on the server (a known service by its pattern, another by what the person marks), the parts kept in the secret store, only the template and the references come back
  const splitWebhook = useMutation({
    mutationFn: (marked?: { pathSegments: number[]; queryKeys: string[] }) => outsideApi.webhook(selected!, { url: pasted, ...marked }),
    onSuccess: (r) => { setSplit(r); setMarking(null); setPasted(''); setYaml(webhookYaml(selected!, r)); setProblems([]); refresh() },
    onError: (e) => {
      const body = (e as { body?: { detected?: boolean } } | null)?.body
      const choices = webhookChoices(pasted)
      if (body?.detected === false && choices) setMarking({ choices, segments: [], keys: [] })
    },
  })
  const isWebhookFile = /^webhook:/m.test(yaml)
  const shown = problems.length > 0 ? problems : refusedDiagnostics(save.error)
  const severityOf = (d: Diagnostic) => (d.severity === 'error' ? 'error' as const : d.severity === 'warning' ? 'warning' as const : 'info' as const)
  const rows = list.data?.connections ?? []

  return (
    <section style={{ marginTop: 24 }} data-testid="outside-connections">
      <div className="page-head">
        <h2 className="page-title" style={{ fontSize: 16 }}>Outside systems, model providers, mail servers and webhooks</h2>
        <span className="page-note">The connections this console keeps: an API, a model provider, a mail server, a webhook. A file names the <em>reference</em> of each secret; the secret is kept in the console&apos;s secret store and is never shown.</span>
      </div>
      <ErrorBanner error={list.error} />
      {list.isSuccess && rows.length === 0 && <div className="empty" data-testid="outside-none">No connection to an outside system yet.</div>}
      {rows.length > 0 && (
        <div className="card flush" data-testid="outside-rows">
          <div className="grid-head" style={{ gridTemplateColumns: columns, gap: 14 }}><span>Name</span><span>Kind</span><span>Address and sign-in</span><span /></div>
          {rows.map((c) => (
            <div key={c.name} className="grid-row" style={{ gridTemplateColumns: columns, gap: 14 }} data-testid="outside-row" data-name={c.name} data-kind={c.kind ?? ''}>
              <span className="mono">{c.name}</span>
              <span data-testid="outside-kind">{c.kind ? KIND_LABEL[c.kind] ?? c.kind : 'unreadable'}</span>
              <span data-testid="outside-detail">{outsideDetail(c)}{secretProblems(c).length > 0 && <span className="page-note" data-testid="outside-secret-problem">{' '}A secret cannot be read.</span>}</span>
              <span className="row" style={{ gap: 8 }}><button type="button" className="btn" onClick={() => { test.reset(); setSelected(selected === c.name ? null : c.name) }} data-testid="outside-edit">{selected === c.name ? 'Close' : isAdmin ? 'Edit' : 'View'}</button></span>
            </div>
          ))}
        </div>
      )}
      {isAdmin && (
        <form className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap', marginTop: 12 }} onSubmit={(e) => { e.preventDefault(); if (isConnectionName(newName)) start.mutate() }} data-testid="outside-new">
          <Field label="New connection">
            <input className="input mono" value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="a name, such as crm" data-testid="outside-new-name" />
          </Field>
          <Field label="Kind">
            <select className="input" value={newChoice} onChange={(e) => setNewChoice(e.target.value)} data-testid="outside-new-kind">{NEW_CHOICES.map((c) => <option key={c.id} value={c.id}>{c.label}</option>)}</select>
          </Field>
          <button type="submit" className="btn" disabled={!isConnectionName(newName) || start.isPending} data-testid="outside-new-start">Start from a template</button>
          <span className="page-note" data-testid="outside-new-meaning">{choice.meaning}</span>
        </form>
      )}
      <ErrorBanner error={start.error} />
      {selected && (
        <div className="card" style={{ marginTop: 16 }} data-testid="outside-editor">
          <h3 className="page-title" style={{ fontSize: 15 }}>{`Connection ${selected}`}{connection ? '' : ' (not saved yet)'}</h3>
          <CodeEditor value={yaml} language="yaml" onChange={(v) => { setYaml(v); setProblems([]) }} diagnostics={shown.map((d) => ({ line: d.line, column: d.column, message: d.message, severity: severityOf(d) }))} readOnly={!isAdmin} testId="outside-yaml" />
          <div data-testid="outside-diagnostics">{shown.map((d, i) => <div key={i} data-testid="outside-diagnostic" data-severity={d.severity}><span className="mono">{d.code}</span> line {d.line}: {d.message}{d.fix ? ` ${d.fix}` : ''}</div>)}</div>
          {isAdmin && (
            <div className="row" style={{ gap: 8, marginTop: 8 }}>
              <button type="button" className="btn" onClick={() => check.mutate()} data-testid="outside-check">Check</button>
              <button type="button" className="btn btn-primary" onClick={() => save.mutate()} disabled={save.isPending} data-testid="outside-save">Save</button>
              {connection && (connection.kind === 'http' || connection.kind === 'smtp') && <button type="button" className="btn" onClick={() => test.mutate()} disabled={test.isPending} data-testid="outside-test">Test</button>}
              {connection && <button type="button" className="btn" onClick={() => remove.mutate()} data-testid="outside-remove">Remove</button>}
            </div>
          )}
          {test.data && (
            <div className="empty" role="status" data-testid="outside-test-result" data-ok={test.data.ok} data-signin={test.data.signIn} style={{ marginTop: 8 }}>
              <strong>{testSummary(test.data)}</strong> {test.data.message}
            </div>
          )}
          {isAdmin && isWebhookFile && (
            <div style={{ marginTop: 12 }} data-testid="outside-webhook">
              <strong>Paste the webhook&apos;s address</strong>
              <div className="page-note">It is split into the part that stays visible in the file and the secret parts (a token, a signature), which are kept in the secret store. Only the template is shown afterwards.</div>
              <form className="row" style={{ gap: 8, marginTop: 4 }} onSubmit={(e) => { e.preventDefault(); if (pasted.trim()) splitWebhook.mutate(undefined) }}>
                <input type="password" autoComplete="off" className="input" style={{ minWidth: 360 }} value={pasted} onChange={(e) => { setPasted(e.target.value); setMarking(null) }} placeholder="https://hooks.slack.com/services/..." data-testid="outside-webhook-url" />
                <button type="submit" className="btn" disabled={!pasted.trim() || splitWebhook.isPending} data-testid="outside-webhook-split">Split and keep the secret parts</button>
              </form>
              {marking && (
                <div className="empty" style={{ marginTop: 8 }} data-testid="outside-webhook-marking">
                  The editor does not know this service. Tick what is secret (the values are not shown here):
                  <div>{Array.from({ length: marking.choices.pathSegments }, (_, i) => (
                    <label key={i} style={{ marginRight: 12 }}><input type="checkbox" checked={marking.segments.includes(i)} onChange={(e) => setMarking({ ...marking, segments: e.target.checked ? [...marking.segments, i] : marking.segments.filter((x) => x !== i) })} data-testid={`outside-webhook-segment-${i}`} /> path segment {i + 1}</label>
                  ))}</div>
                  <div>{marking.choices.queryKeys.map((k) => (
                    <label key={k} style={{ marginRight: 12 }}><input type="checkbox" checked={marking.keys.includes(k)} onChange={(e) => setMarking({ ...marking, keys: e.target.checked ? [...marking.keys, k] : marking.keys.filter((x) => x !== k) })} data-testid={`outside-webhook-key-${k}`} /> query value <span className="mono">{k}</span></label>
                  ))}</div>
                  <button type="button" className="btn" style={{ marginTop: 6 }} disabled={marking.segments.length + marking.keys.length === 0 || splitWebhook.isPending} onClick={() => splitWebhook.mutate({ pathSegments: marking.segments, queryKeys: marking.keys })} data-testid="outside-webhook-split-chosen">Keep the ticked parts as secrets</button>
                </div>
              )}
              {split && (
                <div className="hint" role="status" style={{ marginTop: 8 }} data-testid="outside-webhook-detected" data-service={split.service}>
                  {splitSummary(split)}
                  {split.format && yamlWebhookFormat(yaml) !== null && yamlWebhookFormat(yaml) !== split.format && <span className="error" data-testid="outside-webhook-mismatch"> The file says format {yamlWebhookFormat(yaml)}, which is not what {split.service} takes.</span>}
                  {' '}Save the file to keep it.
                </div>
              )}
            </div>
          )}
          {connection && connection.secrets.map((s) => (
            <div key={s.path} style={{ marginTop: 10 }} data-testid="outside-secret" data-field={s.field} data-available={s.available}>
              <strong>{secretLabel(s.field, s.path)}</strong> <span className="mono">{s.reference}</span>: {s.available ? 'it can be read.' : s.problem ?? 'not set.'}
              {isAdmin && keptHere(connection.name, s) && (
                <form className="row" style={{ gap: 8, marginTop: 4 }} onSubmit={(e) => { e.preventDefault(); if (secrets[s.field]) keep.mutate(s.field) }}>
                  <input type="password" autoComplete="off" className="input" style={{ minWidth: 320 }} value={secrets[s.field] ?? ''} onChange={(e) => setSecrets((all) => ({ ...all, [s.field]: e.target.value }))}
                    placeholder="type it; it is kept in the secret store and not shown again" data-testid="outside-secret-input" />
                  <button type="submit" className="btn" disabled={!secrets[s.field]} data-testid="outside-secret-save">Keep it</button>
                </form>
              )}
            </div>
          ))}
          <ErrorBanner error={check.error ?? remove.error ?? keep.error ?? test.error ?? (marking ? null : splitWebhook.error)} />
        </div>
      )}
    </section>
  )
}
