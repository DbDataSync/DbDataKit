import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CodeEditor, ErrorBanner, Field, useIsAdmin } from '@dbdatakit/shell'
import type { Diagnostic } from '../agents'
import {
  KIND_LABEL, KIND_MEANING, OUTSIDE_KINDS, SECRET_LABEL, isConnectionName, keptHere, outsideApi, outsideDetail, refusedDiagnostics, secretProblems, testSummary,
  type OutsideConnection, type OutsideKind,
} from '../outsideConnections'

const columns = '1.6fr 1fr 3fr 1.4fr'

/**
 * The connections of outside systems the console keeps (plan 27a IX01): an API (kind `http`) and a model provider (kind `model-provider`), each a `connection/2` file with its address and the *references* of its secrets. A person edits the file as text with the loader's
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
  const [newKind, setNewKind] = useState<OutsideKind>('http')
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
    mutationFn: () => outsideApi.template(newKind, newName),
    onSuccess: (r) => { setSelected(newName); setYaml(r.yaml); setProblems([]); setNewName('') },
  })
  const shown = problems.length > 0 ? problems : refusedDiagnostics(save.error)
  const severityOf = (d: Diagnostic) => (d.severity === 'error' ? 'error' as const : d.severity === 'warning' ? 'warning' as const : 'info' as const)
  const rows = list.data?.connections ?? []

  return (
    <section style={{ marginTop: 24 }} data-testid="outside-connections">
      <div className="page-head">
        <h2 className="page-title" style={{ fontSize: 16 }}>Outside systems and model providers</h2>
        <span className="page-note">The connections this console keeps: an API, a model provider. A file names the <em>reference</em> of each secret; the secret is kept in the console&apos;s secret store and is never shown.</span>
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
            <select className="input" value={newKind} onChange={(e) => setNewKind(e.target.value as OutsideKind)} data-testid="outside-new-kind">{OUTSIDE_KINDS.map((k) => <option key={k} value={k}>{KIND_LABEL[k]}</option>)}</select>
          </Field>
          <button type="submit" className="btn" disabled={!isConnectionName(newName) || start.isPending} data-testid="outside-new-start">Start from a template</button>
          <span className="page-note" data-testid="outside-new-meaning">{KIND_MEANING[newKind]}</span>
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
              {connection && connection.kind === 'http' && <button type="button" className="btn" onClick={() => test.mutate()} disabled={test.isPending} data-testid="outside-test">Test</button>}
              {connection && <button type="button" className="btn" onClick={() => remove.mutate()} data-testid="outside-remove">Remove</button>}
            </div>
          )}
          {test.data && (
            <div className="empty" role="status" data-testid="outside-test-result" data-ok={test.data.ok} data-signin={test.data.signIn} style={{ marginTop: 8 }}>
              <strong>{testSummary(test.data)}</strong> {test.data.message}
            </div>
          )}
          {connection && connection.secrets.map((s) => (
            <div key={s.path} style={{ marginTop: 10 }} data-testid="outside-secret" data-field={s.field} data-available={s.available}>
              <strong>{SECRET_LABEL[s.field] ?? s.field}</strong> <span className="mono">{s.reference}</span>: {s.available ? 'it can be read.' : s.problem ?? 'not set.'}
              {isAdmin && keptHere(connection.name, s) && (
                <form className="row" style={{ gap: 8, marginTop: 4 }} onSubmit={(e) => { e.preventDefault(); if (secrets[s.field]) keep.mutate(s.field) }}>
                  <input type="password" autoComplete="off" className="input" style={{ minWidth: 320 }} value={secrets[s.field] ?? ''} onChange={(e) => setSecrets((all) => ({ ...all, [s.field]: e.target.value }))}
                    placeholder="type it; it is kept in the secret store and not shown again" data-testid="outside-secret-input" />
                  <button type="submit" className="btn" disabled={!secrets[s.field]} data-testid="outside-secret-save">Keep it</button>
                </form>
              )}
            </div>
          ))}
          <ErrorBanner error={check.error ?? remove.error ?? keep.error ?? test.error} />
        </div>
      )}
    </section>
  )
}
