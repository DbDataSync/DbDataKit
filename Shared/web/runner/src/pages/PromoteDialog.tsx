import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { Modal } from './Dialogs'
import { emptyPromoteForm, parseBindings, promoteApi, promoteBlock, promoteBody, promotionText, targetServers, type ConsoleServer, type PromoteAnswer, type PromoteForm } from '../promote'

export interface PromoteDialogViewProps {
  name: string
  /** The runners it can go to. */
  servers: ConsoleServer[]
  /** The channels of this package and the versions it has: what can be chosen to read from. */
  channels: string[]
  versions: number[]
  form: PromoteForm
  busy: boolean
  failure: unknown
  result: PromoteAnswer | null
  onForm: (form: PromoteForm) => void
  onSend: () => void
  onCancel: () => void
}

/** The promotion without its calls: the target runner and channel, the version, serve or stage, and the target channel's own connections. Hook-free so a test can draw it and press its buttons. */
export function PromoteDialogView(p: PromoteDialogViewProps) {
  const block = promoteBlock(p.form)
  const set = (over: Partial<PromoteForm>) => p.onForm({ ...p.form, ...over })
  const target = p.servers.find((s) => s.id === p.form.to)
  const bindings = parseBindings(p.form.bindings)
  return (
    <Modal label={`Promote ${p.name} to another runner`} testId="runner-promote-dialog" onCancel={p.onCancel}>
      <span className="hint">The same version is sent as you: the files the target already holds are not sent again, its signature travels and the target checks it against the keys it trusts. The target refuses a connection slot its channel does not bind and a dependency that is not active there, naming it. Each runner numbers its versions on its own.</span>
      <div className="grid-2" style={{ gap: 12, marginTop: 12 }}>
        <label className="field"><span>Runner it goes to</span>
          <select className="input" value={p.form.to} onChange={(e) => set({ to: e.target.value })} data-testid="runner-promote-target">
            <option value="">Choose...</option>
            {p.servers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select>
        </label>
        <label className="field"><span>Channel it is read from here</span>
          <select className="input" value={p.form.fromChannel} onChange={(e) => set({ fromChannel: e.target.value })} data-testid="runner-promote-from">
            {p.channels.map((c) => <option key={c} value={c}>{c}</option>)}
          </select>
        </label>
        <label className="field"><span>Channel it goes to there</span>
          <input className="input mono" value={p.form.toChannel} onChange={(e) => set({ toChannel: e.target.value })} data-testid="runner-promote-to-channel" />
        </label>
        <label className="field"><span>Version (empty: the one {p.form.fromChannel || 'the channel'} serves)</span>
          <select className="input" value={p.form.version} onChange={(e) => set({ version: e.target.value })} data-testid="runner-promote-version">
            <option value="">The one it serves</option>
            {p.versions.map((v) => <option key={v} value={String(v)}>v{v}</option>)}
          </select>
        </label>
        <label className="field"><span>There it is</span>
          <select className="input" value={p.form.mode} onChange={(e) => set({ mode: e.target.value as PromoteForm['mode'] })} data-testid="runner-promote-mode">
            <option value="">As the target decides (its default for a package)</option>
            <option value="stage">Staged</option>
            <option value="serve">Served at once</option>
          </select>
        </label>
      </div>
      <label className="field" style={{ marginTop: 12 }}><span>Connections of {p.form.toChannel || 'the target channel'} there (one <code>slot=connection</code> per line, optional: what is bound there stays)</span>
        <textarea className="input mono" rows={3} value={p.form.bindings} onChange={(e) => set({ bindings: e.target.value })} data-testid="runner-promote-bindings" />
      </label>
      {!bindings.ok && <div className="field-error" role="alert" data-testid="runner-promote-bindings-problem">{bindings.problem}</div>}
      {p.servers.length === 0 && <div className="empty" data-testid="runner-promote-none">The console manages no other runner to promote to.</div>}
      <ErrorBanner error={p.failure} />
      {p.result && <div className="hint" role="status" data-testid="runner-promote-done" data-changed={p.result.changed} data-target-version={p.result.targetVersion} data-sent={p.result.sent}>{promotionText(p.result, target?.name ?? 'The target')}</div>}
      <div className="row" style={{ gap: 8, justifyContent: 'flex-end', marginTop: 12 }}>
        {block !== null && !p.busy && !p.result && <span className="hint" data-testid="runner-promote-why">{block}</span>}
        <button type="button" className="btn" onClick={p.onCancel} data-testid="runner-promote-close">{p.result ? 'Close' : 'Cancel'}</button>
        <button type="button" className="btn btn-primary" disabled={block !== null || p.busy} onClick={p.onSend} data-testid="runner-promote-go">{p.busy ? 'Promoting...' : 'Promote'}</button>
      </div>
    </Modal>
  )
}

/** The Promote action of the Packages screen (plan 30 PB06), for a runner reached through a console: the console reads this runner and writes the other as the signed-in person. */
export function PromoteDialog({ here, name, channels, versions, from, version, onCancel, onDone }: { here: string; name: string; channels: string[]; versions: number[]; from?: string; version?: number; onCancel: () => void; onDone: () => void }) {
  const [form, setForm] = useState<PromoteForm>({ ...emptyPromoteForm, fromChannel: from ?? channels[0] ?? '', toChannel: from ?? channels[0] ?? '', version: version === undefined ? '' : String(version) })
  const servers = useQuery({ queryKey: ['console', 'servers'], queryFn: promoteApi.servers })
  const send = useMutation({ mutationFn: () => promoteApi.promote(promoteBody(here, name, form)), onSuccess: onDone })
  return (
    <PromoteDialogView name={name} servers={targetServers(servers.data ?? [], here)} channels={channels} versions={versions} form={form} busy={send.isPending} failure={send.error ?? servers.error} result={send.data ?? null}
      onForm={(next) => { send.reset(); setForm(next) }} onSend={() => send.mutate()} onCancel={onCancel} />
  )
}
