import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, Field, useIsAdmin } from '@dbdatakit/shell'
import { consoleApi, instancesKey, type InstanceSummary } from '../api'
import { PendingServers } from './PendingServers'
import { Link } from 'react-router-dom'
import { ConsoleShell } from '../ConsoleShell'

const COLUMNS = '1.4fr 1.4fr 1.8fr 2fr 1fr'

/** What the person should read about a server: how it answered, and if it did not, why. */
function statusOf(instance: InstanceSummary): { text: string; tone: 'ok' | 'warn' | 'bad' } {
  if (!instance.reachable) return { text: `Unreachable: ${instance.problem ?? 'no answer'}`, tone: 'bad' }
  if (instance.ready === false) return { text: 'Not ready', tone: 'warn' }
  return { text: 'Ready', tone: 'ok' }
}

/**
 * The servers this console manages (plan 13, U12): how each answers, a form to pair another with the single-use code its administrator made, and forgetting one.
 * Administrators only; the API enforces it, and this screen says so rather than showing a viewer a list that would 403.
 */
export function InstancesPage() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  // Asked again every few seconds, so a server that stops answering shows as unreachable without anyone pressing anything.
  const instances = useQuery({ queryKey: instancesKey, queryFn: consoleApi.instances.list, enabled: isAdmin, refetchInterval: 10_000 })
  const [url, setUrl] = useState('')
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const pair = useMutation({
    mutationFn: () => consoleApi.instances.pair(url.trim(), code.trim(), name.trim() || null),
    onSuccess: async () => {
      setUrl(''); setCode(''); setName('')
      await queryClient.invalidateQueries({ queryKey: instancesKey })
    },
  })
  const [linkFrom, setLinkFrom] = useState('')
  const [linkTo, setLinkTo] = useState('')
  const link = useMutation({
    mutationFn: () => consoleApi.instances.link(linkFrom, linkTo, null),
  })
  const forget = useMutation({
    mutationFn: (id: string) => consoleApi.instances.forget(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: instancesKey }),
  })

  const reachable = (instances.data ?? []).filter((i) => i.reachable)
  const builds = reachable.filter((i) => i.product === 'dbdatabuild')
  const proxies = reachable.filter((i) => i.product === 'dbdataproxy')

  if (!isAdmin) {
    return (
      <ConsoleShell crumbs={[{ label: 'Servers', heading: true }]}>
        <div className="pane"><div className="empty">This screen is for administrators.</div></div>
      </ConsoleShell>
    )
  }

  return (
    <ConsoleShell crumbs={[{ label: 'Servers', heading: true }]}>
      <div className="pane" data-testid="instances-page">
        <div className="page-head">
          <h1 className="page-title">Servers</h1>
          <span className="page-note">The DbData servers this console manages. A server is paired once with a code its administrator makes (<span className="mono">server pair</span>); the console then pins its certificate.</span>
        </div>

        <ErrorBanner error={instances.error ?? forget.error} />

        <PendingServers />

        <div className="card flush" data-testid="instances-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Name</span><span>Product</span><span>Status</span><span>Address</span><span></span>
          </div>
          {instances.isLoading && <div className="empty">Loading…</div>}
          {!instances.isLoading && (instances.data ?? []).length === 0 && <div className="empty" data-testid="instances-empty">No server is paired yet.</div>}
          {(instances.data ?? []).map((instance) => {
            const status = statusOf(instance)
            return (
              <div key={instance.id} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 14 }} data-testid={`instance-row-${instance.name}`}>
                <span>{instance.name}</span>
                <span className="mono">{instance.product ?? '—'} {instance.version ?? ''}</span>
                <span data-testid="instance-status" data-tone={status.tone}>{status.text}</span>
                <span className="mono">{instance.url}</span>
                <span className="row" style={{ gap: 8 }}>
                  {instance.reachable && (instance.product === 'dbdatasync' || instance.product === 'dbdatabuild' || instance.product === 'dbdataproxy') && (
                    <Link to={`/servers/${instance.id}`} className="btn btn-primary" data-testid="instance-open-link">Open</Link>
                  )}
                  <button type="button" className="btn" disabled={forget.isPending} onClick={() => forget.mutate(instance.id)} data-testid="instance-forget-button">Forget</button>
                </span>
              </div>
            )
          })}
        </div>

        <div className="card" style={{ marginTop: 20 }}>
          <div className="card-head"><span className="card-title">Pair a server</span></div>
          <div className="card-body">
            <ErrorBanner error={pair.error} />
            <Field label="Address">
              <input className="input" placeholder="https://build.example:8470" value={url} onChange={(e) => setUrl(e.target.value)} data-testid="pair-url-input" />
            </Field>
            <Field label="Pairing code">
              <span className="hint">Made on the server with <span className="mono">server pair</span>; it works once.</span>
              <input className="input mono" value={code} onChange={(e) => setCode(e.target.value)} data-testid="pair-code-input" />
            </Field>
            <Field label="Name (optional)">
              <input className="input" value={name} onChange={(e) => setName(e.target.value)} data-testid="pair-name-input" />
            </Field>
            <button type="button" className="btn btn-primary" disabled={pair.isPending || !url.trim() || !code.trim()} onClick={() => pair.mutate()} data-testid="pair-button">
              {pair.isPending ? 'Pairing…' : 'Pair'}
            </button>
          </div>
        </div>

        {builds.length > 0 && proxies.length > 0 && (
          <div className="card" style={{ marginTop: 20 }} data-testid="link-card">
            <div className="card-head"><span className="card-title">Link a Build server to a proxy</span></div>
            <div className="card-body">
              <span className="hint">Lets the Build server publish to the proxy. The console makes the pairing code and hands it over; nobody types it.</span>
              <ErrorBanner error={link.error} />
              <Field label="Build server">
                <select className="input" value={linkFrom} onChange={(e) => { setLinkFrom(e.target.value); link.reset() }} data-testid="link-from-select">
                  <option value="">Choose…</option>
                  {builds.map((i) => <option key={i.id} value={i.id}>{i.name}</option>)}
                </select>
              </Field>
              <Field label="Proxy">
                <select className="input" value={linkTo} onChange={(e) => { setLinkTo(e.target.value); link.reset() }} data-testid="link-to-select">
                  <option value="">Choose…</option>
                  {proxies.map((i) => <option key={i.id} value={i.id}>{i.name}</option>)}
                </select>
              </Field>
              <button type="button" className="btn btn-primary" disabled={link.isPending || !linkFrom || !linkTo} onClick={() => link.mutate()} data-testid="link-button">
                {link.isPending ? 'Linking…' : 'Link'}
              </button>
              {link.isSuccess && <div className="hint" data-testid="link-done">{link.data.from} can now publish to {link.data.to} as “{link.data.name}”.</div>}
            </div>
          </div>
        )}
      </div>
    </ConsoleShell>
  )
}
