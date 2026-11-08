import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useIsAdmin } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import { GatewayShell } from '../GatewayShell'
import { afterCall, checkSecret, passwordLine, sorted, type SecretForm } from '../secrets'
import type { SecretItem, SecretWritten } from '../types'

export interface SecretsViewProps {
  items: SecretItem[] | undefined
  loading: boolean
  loadError: string | null
  form: SecretForm
  busy: boolean
  /** What the last set did, to show the reference to write, or null. */
  written: SecretWritten | null
  /** The server's refusal of the last call, in its words, or null. */
  refusal: string | null
  /** The secret the person has been asked to confirm removing. */
  confirming: string | null
  onForm: (form: SecretForm) => void
  onSet: () => void
  onAskRemove: (name: string) => void
  onRemove: (name: string) => void
  onKeep: () => void
}

/** The secrets this gateway keeps for the console (plan 33, UE16): names and references, a value written and never shown again. Draws what it is given. */
export function SecretsView(p: SecretsViewProps) {
  const items = sorted(p.items ?? [])
  const check = checkSecret(p.form, items)
  return (
    <div className="pane" data-testid="gateway-secrets">
      <div className="page-head">
        <h1 className="page-title">Secrets</h1>
        <span className="page-note">Passwords a data source's file refers to, kept encrypted here. A value is written once and never shown again; a data source names the reference, not the password.</span>
      </div>
      <div className="card flush" data-testid="secrets-table">
        <div className="grid-head" style={{ gridTemplateColumns: '2fr 5fr 2fr', gap: 14 }}><span>Name</span><span>Reference to write in a data source</span><span /></div>
        {p.loading && <div className="empty" role="status" data-testid="secrets-loading">Reading the secrets…</div>}
        {p.loadError && <div className="empty" role="alert" data-testid="secrets-error">The secrets could not be listed: {p.loadError}</div>}
        {!p.loading && !p.loadError && items.length === 0 && <div className="empty" data-testid="secrets-empty">No secret is kept yet. Set one below, then name its reference in a data source's file.</div>}
        {items.map((s) => (
          <div key={s.name} className="grid-row auto" style={{ gridTemplateColumns: '2fr 5fr 2fr', gap: 14 }} data-testid={`secret-${s.name}`}>
            <span className="mono">{s.name}</span>
            <code data-testid={`secret-reference-${s.name}`}>{s.reference ? passwordLine(s.reference) : 'the reference is not listed by this gateway'}</code>
            {p.confirming === s.name
              ? (
                <span className="row" style={{ gap: 6 }}>
                  <button type="button" className="btn btn-primary" disabled={p.busy} onClick={() => p.onRemove(s.name)} data-testid="secret-remove-confirm">Remove</button>
                  <button type="button" className="btn" onClick={p.onKeep} data-testid="secret-remove-cancel">Keep</button>
                </span>
              )
              : <button type="button" className="btn" disabled={p.busy} onClick={() => p.onAskRemove(s.name)} data-testid={`secret-remove-${s.name}`}>Remove…</button>}
          </div>
        ))}
        {p.confirming && <p className="hint" style={{ padding: '8px 14px' }} data-testid="secret-remove-ask">A data source whose file names {p.confirming} cannot connect once it is removed.</p>}
      </div>

      <div className="card" style={{ marginTop: 20 }}>
        <div className="card-head"><span className="card-title">{check.replaces ? 'Replace a secret' : 'Set a secret'}</span></div>
        <div className="card-body">
          <form className="row" style={{ gap: 10, flexWrap: 'wrap' }} data-testid="secret-form" onSubmit={(e) => { e.preventDefault(); if (check.ok) p.onSet() }}>
            <input className="input" aria-label="Name" placeholder="name, e.g. erp-login" value={p.form.name} aria-invalid={check.nameProblem ? true : undefined} onChange={(e) => p.onForm({ ...p.form, name: e.target.value })} data-testid="secret-name" />
            <input className="input" type="password" autoComplete="new-password" aria-label="Value" placeholder={check.replaces ? 'new value' : 'value'} value={p.form.value} onChange={(e) => p.onForm({ ...p.form, value: e.target.value })} data-testid="secret-value" />
            <button type="submit" className="btn btn-primary" disabled={p.busy || !check.ok} title={check.why ?? (check.replaces ? 'Replace the value' : 'Keep the value')} data-testid="secret-set">{check.replaces ? 'Replace' : 'Set'}</button>
          </form>
          {check.why && <p className="hint" style={{ marginTop: 8 }} data-testid="secret-why">{check.why}</p>}
          {p.refusal && <p className="hint" role="alert" style={{ marginTop: 8 }} data-testid="secret-refused">{p.refusal}</p>}
          {p.written && (
            <p className="hint" role="status" style={{ marginTop: 8 }} data-testid="secret-written">
              {p.written.replaced ? `The value of ${p.written.name} was replaced.` : `${p.written.name} is kept.`} Write <code data-testid="secret-written-reference">{passwordLine(p.written.reference)}</code> in the data source's file. The value is not shown again.
            </p>
          )}
        </div>
      </div>
    </div>
  )
}

/** The secrets screen: lists, sets or replaces, removes. The Gateway keeps them under the console's own name, needs the admin role for every call, audits names and never values. */
export function SecretsPage() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const list = useQuery({ queryKey: ['gateway', 'secrets'], queryFn: gatewayApi.secrets.list, enabled: isAdmin, retry: false })
  const [form, setForm] = useState<SecretForm>({ name: '', value: '' })
  const [written, setWritten] = useState<SecretWritten | null>(null)
  const [refusal, setRefusal] = useState<string | null>(null)
  const [confirming, setConfirming] = useState<string | null>(null)
  const call = useMutation({ mutationFn: (action: () => Promise<unknown>) => action() })

  if (!isAdmin) return <GatewayShell crumbs={[{ label: 'Secrets', heading: true }]}><div className="pane"><div className="empty">This screen is for administrators.</div></div></GatewayShell>
  const settle = async (action: () => Promise<unknown>, done: () => void) => {
    setRefusal(null)
    try {
      await call.mutateAsync(action)
      done()
      await queryClient.invalidateQueries({ queryKey: ['gateway', 'secrets'] })
    } catch (e) {
      setWritten(null)
      setRefusal(e instanceof Error ? e.message : String(e))
    }
  }
  return (
    <GatewayShell crumbs={[{ label: 'Secrets', heading: true }]}>
      <SecretsView
        items={list.data} loading={list.isPending} loadError={list.error ? (list.error instanceof Error ? list.error.message : String(list.error)) : null}
        form={form} busy={call.isPending} written={written} refusal={refusal} confirming={confirming} onForm={setForm}
        onSet={() => {
          const sent = { name: form.name.trim(), value: form.value }
          setForm((f) => afterCall(f, false))   // the value leaves the form as it is sent and is never kept; a refusal leaves the name to correct
          void settle(async () => setWritten(await gatewayApi.secrets.set(sent.name, sent.value)), () => setForm((f) => afterCall(f, true)))
        }}
        onAskRemove={setConfirming}
        onRemove={(name) => void settle(() => gatewayApi.secrets.remove(name), () => { setConfirming(null); setWritten(null) })}
        onKeep={() => setConfirming(null)}
      />
    </GatewayShell>
  )
}
