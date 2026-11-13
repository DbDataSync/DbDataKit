import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, Field } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { bindingsApi } from '../bindings'
import { addressOf, canRemove, describeUse, filterInventory, loadInventory, productLabel, removeConnection, runnerCopyState, type InventoryRow } from '../connectionInventory'

const columns = '1.1fr 1.3fr 1.4fr 1.6fr 1.1fr 3fr 0.7fr'

/**
 * Where each connection is used (plan 28c E23, ED17): one table over the paired Build and Sync servers and the runners, each connection with its address, the reference of its secret (never a value) and the processes that use it, with the login each
 * needs. "Where is this connection used?" is the first filter, "what does this task connect to?" the second. Removing a connection is the server's call: one a process uses is refused, naming the user, and the sentence is shown here.
 */
export function ConnectionInventory() {
  const client = useQueryClient()
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const servers = instances.data ?? []
  const view = useQuery({
    queryKey: ['connection-inventory', servers.map((s) => `${s.id}:${s.reachable}`).join(',')],
    queryFn: () => loadInventory(servers),
    enabled: instances.isSuccess,
    refetchInterval: 30_000,
  })
  // the console's own connections with their versions, to say whether a runner's copy is the console's file (the Bindings page's comparison)
  const consoleView = useQuery({ queryKey: ['bindings'], queryFn: bindingsApi.get })
  const consoleConnections = consoleView.data?.connections ?? []
  const [connection, setConnection] = useState('')
  const [server, setServer] = useState('')
  const [user, setUser] = useState('')
  const [refused, setRefused] = useState<string | null>(null)
  const remove = useMutation({
    mutationFn: (row: InventoryRow) => removeConnection(row),
    onSuccess: () => { setRefused(null); client.invalidateQueries({ queryKey: ['connection-inventory'] }) },
    onError: (error) => setRefused(error instanceof Error ? error.message : 'The server refused.'),
  })
  const rows = filterInventory(view.data?.rows ?? [], { connection, user, server })

  return (
    <div style={{ marginTop: 24 }} data-testid="connection-inventory">
      <div className="page-head">
        <h2 className="page-title">Where connections are used</h2>
        <span className="page-note">Every connection of your Build and Sync servers and runners, and the processes that use each. A secret is shown as its reference, never its value.</span>
      </div>
      <ErrorBanner error={instances.error ?? view.error} />
      {(view.data?.problems ?? []).map((p) => <div key={p.server} className="empty" data-testid="inventory-problem">{`${p.server} did not answer: ${p.message}`}</div>)}
      <div className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <Field label="Where is this connection used?">
          <input className="input mono" placeholder="connection name" value={connection} onChange={(e) => setConnection(e.target.value)} data-testid="inventory-filter-connection" />
        </Field>
        <Field label="Exists on">
          <input className="input mono" placeholder="server or runner" value={server} onChange={(e) => setServer(e.target.value)} data-testid="inventory-filter-server" />
        </Field>
        <Field label="Used by: what does this task connect to?">
          <input className="input mono" placeholder="task, plan or replication" value={user} onChange={(e) => setUser(e.target.value)} data-testid="inventory-filter-user" />
        </Field>
      </div>
      {refused && <div className="empty" role="alert" data-testid="inventory-refused">{refused}</div>}
      {view.isSuccess && rows.length === 0 && <div className="empty" data-testid="inventory-none">No connection matches on the servers that answered.</div>}
      {rows.length > 0 && (
        <div className="card flush" data-testid="inventory-rows">
          <div className="grid-head" style={{ gridTemplateColumns: columns, gap: 14 }}><span>Server</span><span>Connection</span><span>Address</span><span>Secret reference</span><span>Runner's copy</span><span>Used by</span><span /></div>
          {rows.map((row) => (
            <div key={row.key} className="grid-row" style={{ gridTemplateColumns: columns, gap: 14 }} data-testid="inventory-row" data-server={row.serverName} data-name={row.name} data-product={row.product}>
              <span>{row.serverName} <span className="page-note">({productLabel[row.product]})</span></span>
              <span className="mono">{row.name}</span>
              <span className="mono" data-testid="inventory-address">{addressOf(row)}</span>
              <span className="mono" data-testid="inventory-secret">{row.secretRef ?? 'none'}</span>
              <span data-testid="inventory-copy" data-source={row.source ?? ''} data-state={runnerCopyState(row, consoleConnections) ?? ''} title={row.version ?? undefined}>
                {row.source === null ? '' : `${row.source}${runnerCopyState(row, consoleConnections) ? `: ${runnerCopyState(row, consoleConnections)}` : ''}`}
              </span>
              <span data-testid="inventory-users">
                {row.usedBy.length === 0 ? 'Not used' : row.usedBy.map((u) => <div key={`${u.process}${u.name}${u.instance}${u.channel}`} data-testid="inventory-use" data-process={u.process} data-use={u.name}>{describeUse(u)}</div>)}
              </span>
              <span>{canRemove(row) && <button type="button" className="btn" disabled={remove.isPending} onClick={() => remove.mutate(row)} data-testid="inventory-remove">Remove</button>}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
