import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, request, useIsAdmin } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { actionsFor, draftBlock, draftBody, emptyItem, gatewayKinds, itemLine, runnerKind, serverProductOfKind, type ItemDraft, type ReleaseView } from '../releases'

const releasesApi = {
  list: () => request<ReleaseView[]>('/api/releases'),
  create: (body: unknown) => request<ReleaseView>('/api/releases', { method: 'POST', body: JSON.stringify(body) }),
  act: (id: string, verb: 'approve' | 'start' | 'resume' | 'abandon') => request<ReleaseView>(`/api/releases/${encodeURIComponent(id)}/${verb}`, { method: 'POST' }),
}

/**
 * Releases (plan 21 CH02): ordered promotions of packages (runners) and definitions (Gateways) between groups with one approval. A draft is made here, approved once (by an administrator who holds the administrator role on every item's target server), started and, when an item fails, resumed at that
 * item. It is a sequence, not a transaction: what the items before the failing one did stays done.
 */
export function ReleasesPage() {
  const isAdmin = useIsAdmin()
  const client = useQueryClient()
  const list = useQuery({ queryKey: ['releases'], queryFn: releasesApi.list, refetchInterval: 5000 })
  const servers = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const serversFor = (kind: string) => (servers.data ?? []).filter((s) => s.product === serverProductOfKind(kind))
  const serverName = (id: string) => servers.data?.find((s) => s.id === id)?.name ?? id
  const [name, setName] = useState('')
  const [items, setItems] = useState<ItemDraft[]>([{ ...emptyItem }])
  const block = draftBlock(name, items)
  const refresh = () => client.invalidateQueries({ queryKey: ['releases'] })
  const create = useMutation({ mutationFn: () => releasesApi.create(draftBody(name, items)), onSuccess: () => { setName(''); setItems([{ ...emptyItem }]); void refresh() } })
  const act = useMutation({ mutationFn: (a: { id: string; verb: 'approve' | 'start' | 'resume' | 'abandon' }) => releasesApi.act(a.id, a.verb), onSettled: () => void refresh() })
  const set = (n: number, over: Partial<ItemDraft>) => setItems(items.map((it, i) => (i === n ? { ...it, ...over } : it)))

  return (
    <ConsoleShell crumbs={[{ label: 'Releases', heading: true }]}>
      <div className="pane" data-testid="releases-page">
        <div className="page-head"><h1 className="page-title">Releases</h1><span className="page-note">Packages and Gateway definitions promoted between groups in order, with one approval. A release is a sequence, not a transaction.</span></div>
        <ErrorBanner error={list.error ?? create.error ?? act.error} />
        {list.isSuccess && list.data.length === 0 && <div className="empty" data-testid="releases-empty">No release yet.</div>}
        {(list.data ?? []).map((r) => {
          const can = actionsFor(r.state)
          return (
            <div key={r.id} className="card" data-testid="release" data-release={r.name} data-state={r.state}>
              <div className="card-body">
                <div className="row-between">
                  <div className="card-title">{r.name} <span className="hint" data-testid="release-state">{r.state}</span></div>
                  {isAdmin && (
                    <span className="row" style={{ gap: 8 }}>
                      {can.approve && <button type="button" className="btn btn-sm" disabled={act.isPending} onClick={() => act.mutate({ id: r.id, verb: 'approve' })} data-testid="release-approve">Approve</button>}
                      {can.start && <button type="button" className="btn btn-sm btn-primary" disabled={act.isPending} onClick={() => act.mutate({ id: r.id, verb: 'start' })} data-testid="release-start">Start</button>}
                      {can.resume && <button type="button" className="btn btn-sm btn-primary" disabled={act.isPending} onClick={() => act.mutate({ id: r.id, verb: 'resume' })} data-testid="release-resume">Resume</button>}
                      {can.abandon && <button type="button" className="btn btn-sm" disabled={act.isPending} onClick={() => act.mutate({ id: r.id, verb: 'abandon' })} data-testid="release-abandon">Abandon</button>}
                    </span>
                  )}
                </div>
                <div className="hint">Made by {r.createdByName}{r.approvedBy ? `, approved by ${r.approvedByName}` : ', not approved yet'}</div>
                {r.stoppedReason && <div className="field-error" role="alert" data-testid="release-stopped">{r.stoppedReason}</div>}
                <div className="card flush">
                  {r.items.map((i) => (
                    <div key={i.position} className="grid-row" style={{ gridTemplateColumns: '0.4fr 4fr 1fr 3fr', gap: 14 }} data-testid="release-item" data-state={i.state}>
                      <span>{i.position}</span><span>{itemLine(i, serverName)}</span><span data-testid="release-item-state">{i.state}</span><span>{i.error ?? ''}</span>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          )
        })}
        {isAdmin && (
          <div className="card" data-testid="release-new">
            <div className="card-body">
              <div className="card-title">New release</div>
              <label className="field"><span>Name</span><input className="input" value={name} onChange={(e) => setName(e.target.value)} data-testid="release-name" /></label>
              {items.map((it, n) => (
                <div key={n} className="row" style={{ gap: 8, flexWrap: 'wrap', marginTop: 8 }} data-testid="release-item-form">
                  <select className="input" aria-label="Kind" value={it.kind} onChange={(e) => set(n, { kind: e.target.value, source: '', target: '' })} data-testid="release-item-kind"><option value={runnerKind}>Runner package</option>{gatewayKinds.map((k) => <option key={k} value={k}>Gateway {k}</option>)}</select>
                  <select className="input" aria-label="Read from" value={it.source} onChange={(e) => set(n, { source: e.target.value })} data-testid="release-item-source"><option value="">Same server</option>{serversFor(it.kind).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</select>
                  <select className="input" aria-label="Goes to" value={it.target} onChange={(e) => set(n, { target: e.target.value })} data-testid="release-item-target"><option value="">Goes to...</option>{serversFor(it.kind).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</select>
                  <input className="input mono" placeholder={it.kind === runnerKind ? 'package' : 'definition'} value={it.name} onChange={(e) => set(n, { name: e.target.value })} data-testid="release-item-name" />
                  <input className="input mono" placeholder="version" size={6} value={it.version} onChange={(e) => set(n, { version: e.target.value })} data-testid="release-item-version" />
                  <input className="input mono" placeholder="from channel" size={10} value={it.fromChannel} onChange={(e) => set(n, { fromChannel: e.target.value })} data-testid="release-item-from" />
                  <input className="input mono" placeholder="to channel" size={10} value={it.toChannel} onChange={(e) => set(n, { toChannel: e.target.value })} data-testid="release-item-to" />
                  {items.length > 1 && <button type="button" className="btn btn-sm" onClick={() => setItems(items.filter((_, i) => i !== n))} data-testid="release-item-remove">Remove</button>}
                </div>
              ))}
              <div className="row" style={{ gap: 8, marginTop: 12 }}>
                <button type="button" className="btn btn-sm" onClick={() => setItems([...items, { ...emptyItem }])} data-testid="release-item-add">Add an item</button>
                <button type="button" className="btn btn-primary" disabled={block !== null || create.isPending} onClick={() => create.mutate()} data-testid="release-create">Make the draft</button>
                {block !== null && <span className="hint" data-testid="release-why">{block}</span>}
              </div>
            </div>
          </div>
        )}
      </div>
    </ConsoleShell>
  )
}
