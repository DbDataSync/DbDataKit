import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, Field } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { canCopy, copyConnection, copyFailure, keepsConnections, loadConnections, type ConnectionRow, type CopyFailure } from '../connections'
import { ConsoleShell } from '../ConsoleShell'

const columns = '1.2fr 1.6fr 1fr 2.2fr 1fr'
const product: Record<ConnectionRow['product'], string> = { dbdatasync: 'Sync', dbdataproxy: 'Proxy' }

/**
 * The connections of every server that keeps them, in one list (plan 13a, E05): a Sync server's connections and a Proxy's data sources are the same `connection/1` file, so a connection on one can be copied to another. The copy is made by the console as the signed-in person:
 * it reads the file from the first server, leaves out what belongs to that server (its owner, another product's settings), points a password at the target's own secret reference and writes it to the second. The target reads the file with its own strict loader, and what it
 * refuses comes back here with the line of each fault; nothing is written then. A password is never copied: it is a reference, and the target must hold it (a Sync target is given a typed one, once, to store).
 */
export function ConnectionsPage() {
  const client = useQueryClient()
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const servers = instances.data ?? []
  const view = useQuery({
    queryKey: ['connections', servers.map((s) => `${s.id}:${s.reachable}`).join(',')],
    queryFn: () => loadConnections(servers),
    enabled: instances.isSuccess,
    refetchInterval: 30_000,
  })
  const [copying, setCopying] = useState<ConnectionRow | null>(null)
  const rows = view.data?.rows ?? []

  return (
    <ConsoleShell crumbs={[{ label: 'Connections', heading: true }]}>
      <div className="pane" data-testid="connections-page">
        <div className="page-head">
          <h1 className="page-title">Connections</h1>
          <span className="page-note">The connections of your Sync servers and the data sources of your Proxies. Copy one to another server.</span>
        </div>
        <ErrorBanner error={instances.error ?? view.error} />
        {(view.data?.problems ?? []).map((p) => (
          <div key={p.server} className="empty" data-testid="connections-problem">{`${p.server} did not answer: ${p.message}`}</div>
        ))}
        {view.isSuccess && rows.length === 0 && <div className="empty" data-testid="connections-none">No connection on the servers that answered.</div>}
        {rows.length > 0 && (
          <div className="card flush" data-testid="connections-rows">
            <div className="grid-head" style={{ gridTemplateColumns: columns, gap: 14 }}><span>Server</span><span>Name</span><span>Engine</span><span>Detail</span><span /></div>
            {rows.map((row) => (
              <div key={row.key} className="grid-row" style={{ gridTemplateColumns: columns, gap: 14 }} data-testid="connection-row" data-server={row.serverName} data-name={row.name}>
                <span>{row.serverName} <span className="page-note">({product[row.product]})</span></span>
                <span className="mono">{row.name}</span>
                <span className="mono">{row.engine}</span>
                <span data-testid="connection-detail">{row.detail}</span>
                <span className="row" style={{ gap: 8 }}>
                  <button type="button" className="btn" disabled={!row.valid} onClick={() => setCopying(copying?.key === row.key ? null : row)} data-testid="connection-copy-button">Copy to…</button>
                </span>
              </div>
            ))}
          </div>
        )}
        {copying && (
          <CopyPanel
            key={copying.key}
            row={copying}
            targets={servers.filter(keepsConnections)}
            onCopied={() => client.invalidateQueries({ queryKey: ['connections'] })}
            onClose={() => setCopying(null)}
          />
        )}
      </div>
    </ConsoleShell>
  )
}

function CopyPanel({ row, targets, onCopied, onClose }: { row: ConnectionRow; targets: { id: string; name: string; product: string | null }[]; onCopied: () => void; onClose: () => void }) {
  const [target, setTarget] = useState('')
  const [newName, setNewName] = useState(row.name)
  const [password, setPassword] = useState('')
  const targetProduct = targets.find((t) => t.id === target)?.product
  const copy = useMutation({
    mutationFn: () => copyConnection({ from: row.server, name: row.name, to: target, newName, password: targetProduct === 'dbdatasync' ? password : '' }),
    onSuccess: () => { setPassword(''); onCopied() },
  })
  const failure: CopyFailure | null = copy.isError ? copyFailure(copy.error) : null

  return (
    <div className="card" style={{ marginTop: 16 }} data-testid="copy-panel">
      <h2 className="page-title" style={{ fontSize: 16 }}>{`Copy ${row.name} from ${row.serverName}`}</h2>
      <div className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <Field label="To server">
          <select className="input" value={target} onChange={(e) => { setTarget(e.target.value); copy.reset() }} data-testid="copy-target">
            <option value="">Choose a server</option>
            {targets.map((t) => <option key={t.id} value={t.id}>{`${t.name} (${t.product === 'dbdatasync' ? 'Sync' : 'Proxy'})`}</option>)}
          </select>
        </Field>
        <Field label="Called">
          <input className="input mono" value={newName} onChange={(e) => { setNewName(e.target.value); copy.reset() }} data-testid="copy-name" />
        </Field>
        {targetProduct === 'dbdatasync' && (
          <Field label="Password (if it logs in with one)">
            <input className="input" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} data-testid="copy-password" />
          </Field>
        )}
        <button type="button" className="btn btn-primary" disabled={!canCopy(row, target, newName) || copy.isPending} onClick={() => copy.mutate()} data-testid="copy-submit">Copy</button>
        <button type="button" className="btn" onClick={onClose} data-testid="copy-close">Close</button>
      </div>
      <p className="page-note">The file is copied as it is, without its owner and without the settings of another kind of server. The password itself is never copied: the copy refers to a secret the target must hold.</p>
      {copy.data && (
        <div className="empty" role="status" data-testid="copy-done">
          {`Copied to ${copy.data.to} as ${copy.data.name}.`}
          <ul>{copy.data.notes.map((n) => <li key={n} data-testid="copy-note">{n}</li>)}</ul>
          {copy.data.diagnostics.length > 0 && <ul>{copy.data.diagnostics.map((d) => <li key={`${d.code}${d.line}${d.message}`} data-testid="copy-warning">{`Line ${d.line}: ${d.message}`}</li>)}</ul>}
        </div>
      )}
      {failure && (
        <div className="empty" role="alert" data-testid="copy-failed">
          {failure.message}
          {failure.diagnostics.length > 0 && <ul>{failure.diagnostics.map((d) => <li key={`${d.code}${d.line}${d.message}`} data-testid="copy-diagnostic">{`Line ${d.line}: ${d.message}${d.fix ? ` ${d.fix}` : ''}`}</li>)}</ul>}
          {failure.notes.length > 0 && <ul>{failure.notes.map((n) => <li key={n}>{n}</li>)}</ul>}
        </div>
      )}
    </div>
  )
}
