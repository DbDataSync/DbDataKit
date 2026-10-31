import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { useOutletContext } from 'react-router-dom'
import { ApiError, ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { AdoptHint, PairedProxy, PublishAnswer } from '../types'

/** What the person reads when a publish failed: the server's own words (they name the rule or the proxy that refused), or a plain sentence for what it did not say. */
export function publishFailure(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 403 && !/proxy/i.test(error.message)) return 'Publishing needs the approver role on this server.'
    return error.message
  }
  return 'The server did not answer. Try again.'
}

/** What a refusal says an administrator of the proxy can do: take the data source over for this server. Null when the refusal carries no such offer. */
export function adoptHintOf(error: unknown): AdoptHint | null {
  if (!(error instanceof ApiError) || error.status !== 403) return null
  const adopt = (error.body as { adopt?: Partial<AdoptHint> } | null)?.adopt
  return adopt && typeof adopt.client === 'string' && typeof adopt.data_source === 'string' && typeof adopt.call === 'string' ? (adopt as AdoptHint) : null
}

export interface PublishViewProps {
  proxies: PairedProxy[] | undefined
  loading: boolean
  /** Why the list of proxies could not be read, if it could not. */
  listError: unknown
  proxy: string
  connection: string
  dataSource: string
  busy: boolean
  /** The last publish that went through, shown until the next try. */
  done: PublishAnswer | null
  /** The last publish that was refused or failed. */
  failure: unknown
  onProxy: (name: string) => void
  onConnection: (name: string) => void
  onDataSource: (name: string) => void
  onPublish: () => void
}

/** The screen without its calls: the form, what the last publish came to and why a refusal was a refusal. Hook-free, so a test can draw it and press its button without a browser. */
export function PublishView(p: PublishViewProps) {
  const none = !p.loading && !p.listError && (p.proxies?.length ?? 0) === 0
  const ready = p.proxy !== '' && p.connection.trim() !== '' && !p.busy
  const offer = adoptHintOf(p.failure)
  return (
    <div data-testid="build-publish">
      <div className="hint" style={{ marginBottom: 14 }} data-testid="build-publish-about">
        Makes a connection of this project available through a proxy, as a read-only data source. Only the connection&apos;s <strong>read</strong> login is sent, once, to the proxy&apos;s own secret store; the write login never leaves this server.
        Needs the approver role. SQL Server, ODBC, DuckDB, PostgreSQL and Flight SQL connections can be published, the engines a proxy serves; a connection of another engine is refused with a message.
      </div>
      <ErrorBanner error={p.listError} />
      {p.loading && <div className="empty">Loading…</div>}
      {none && <div className="empty" data-testid="build-publish-none">This server is not paired with a proxy. Link it to one on the console&apos;s Servers page.</div>}
      {(p.proxies?.length ?? 0) > 0 && (
        <div className="row" style={{ gap: 12, marginBottom: 14, flexWrap: 'wrap' }}>
          <label className="row" style={{ gap: 8 }}>
            <span className="hint">Proxy</span>
            <select className="input" value={p.proxy} onChange={(e) => p.onProxy(e.target.value)} data-testid="build-publish-proxy">
              <option value="">Choose…</option>
              {p.proxies!.map((x) => <option key={x.name} value={x.name}>{x.name}</option>)}
            </select>
          </label>
          <label className="row" style={{ gap: 8 }}>
            <span className="hint">Connection</span>
            <input className="input mono" value={p.connection} placeholder="a connection of the project" onChange={(e) => p.onConnection(e.target.value)} data-testid="build-publish-connection" style={{ width: 200 }} />
          </label>
          <label className="row" style={{ gap: 8 }}>
            <span className="hint">Data source</span>
            <input className="input mono" value={p.dataSource} placeholder="the connection's name" onChange={(e) => p.onDataSource(e.target.value)} data-testid="build-publish-data-source" style={{ width: 200 }} />
          </label>
          <button type="button" className="btn btn-primary" disabled={!ready} onClick={p.onPublish} data-testid="build-publish-button">{p.busy ? 'Publishing…' : 'Publish'}</button>
        </div>
      )}
      {p.failure != null && <div className="banner error" role="alert" data-testid="build-publish-failed"><span className="mark">!</span><span>{publishFailure(p.failure)}</span></div>}
      {offer && (
        <div className="hint" style={{ marginBottom: 14 }} data-testid="build-publish-adopt">
          This data source was made by hand or by another client. An administrator of the proxy can hand it to this server: <span className="mono">{offer.call}</span> with <span className="mono">{`{"client": "${offer.client}"}`}</span>.
          This server cannot do that for itself.
        </div>
      )}
      {p.done && (
        <div className="card" data-testid="build-publish-done">
          <div className="card-body">
            <span className="mono">{p.done.data_source}</span> is {p.done.created ? 'now' : 'updated'} on <strong>{p.done.proxy}</strong>{p.done.created ? ' (new)' : ''}.
          </div>
        </div>
      )}
    </div>
  )
}

/** Publishing a connection of the project to a proxy this server is paired with (plan 13, T10 and T12). */
export function PublishTab() {
  const { project } = useOutletContext<{ project: string }>()
  const [proxy, setProxy] = useState('')
  const [connection, setConnection] = useState('')
  const [dataSource, setDataSource] = useState('')
  const proxies = useQuery({ queryKey: ['build', 'proxies'], queryFn: () => buildApi.proxies() })
  const publish = useMutation({ mutationFn: () => buildApi.publish(project, proxy, connection.trim(), dataSource.trim() || undefined) })
  return (
    <PublishView
      proxies={proxies.data} loading={proxies.isLoading} listError={proxies.error}
      proxy={proxy} connection={connection} dataSource={dataSource}
      busy={publish.isPending} done={publish.data ?? null} failure={publish.error}
      onProxy={(v) => { setProxy(v); publish.reset() }} onConnection={(v) => { setConnection(v); publish.reset() }} onDataSource={(v) => { setDataSource(v); publish.reset() }}
      onPublish={() => publish.mutate()}
    />
  )
}
