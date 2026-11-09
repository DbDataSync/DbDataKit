import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { Link } from 'react-router-dom'
import { ConsoleShell } from '../ConsoleShell'
import { availableKey, upgradeApi } from '../upgradeApi'
import { ServerUpgrade } from './UpgradeServer'

/**
 * What is available and the upgrade of one server at a time (plan 24a UC08 and UC09): the versions the release feed offers per product, each paired server's current and newest version with the mark for a release outside N and N-1,
 * Check (download, verify, report: nothing changes), and Apply with the drain, swap and health progress and the rollback result. An image or a package install shows its command instead. Administrators only.
 */
export function UpgradesPage() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const available = useQuery({ queryKey: availableKey, queryFn: () => upgradeApi.available(false), enabled: isAdmin, refetchInterval: 60_000, retry: false })
  const refresh = useMutation({
    mutationFn: () => upgradeApi.available(true),
    onSuccess: (data) => queryClient.setQueryData(availableKey, data),
  })
  if (!isAdmin) {
    return (
      <ConsoleShell crumbs={[{ label: 'Upgrades', heading: true }]}>
        <div className="pane"><div className="empty">This screen is for administrators.</div></div>
      </ConsoleShell>
    )
  }
  const data = available.data
  return (
    <ConsoleShell crumbs={[{ label: 'Servers', to: '/instances' }, { label: 'Upgrades', heading: true }]}>
      <div className="pane" data-testid="upgrades-page">
        <div className="page-head">
          <h1 className="page-title">Upgrades</h1>
          <span className="page-note" data-testid="upgrades-feed">
            {data ? <>Releases are read from <span className="mono">{data.feed}</span> ({data.mirror ? 'a mirror' : 'GitHub'}, {data.channel}). This console is {data.consoleVersion}, generation {data.consoleGeneration}. Upgrade the console first, then the servers, one at a time.</> : 'Reading the release feed…'}
          </span>
          <button type="button" className="btn" disabled={refresh.isPending} onClick={() => refresh.mutate()} data-testid="upgrades-refresh">{refresh.isPending ? 'Asking…' : 'Ask the feed again'}</button>
        </div>
        <ErrorBanner error={available.error ?? refresh.error} />
        {data && (
          <>
            <div className="card flush" data-testid="upgrades-products">
              <div className="grid-head" style={{ gridTemplateColumns: '1.4fr 3fr' }}><span>Product</span><span>Versions available</span></div>
              {data.products.length === 0 && <div className="empty" data-testid="upgrades-no-products">No server is paired, so no product is looked up.</div>}
              {data.products.map((p) => (
                <div key={p.product} className="grid-row" style={{ gridTemplateColumns: '1.4fr 3fr' }} data-testid={`upgrades-product-${p.product}`}>
                  <span className="mono">{p.product}</span>
                  <span data-testid={`upgrades-versions-${p.product}`}>
                    {p.releases.length === 0 ? 'none in the feed' : p.releases.map((r, i) => (
                      <span key={r.version} style={{ marginRight: 10 }}>
                        {r.notesUrl ? <a href={r.notesUrl} target="_blank" rel="noreferrer">{r.version}</a> : r.version}{r.channel !== 'stable' ? ` (${r.channel})` : ''}{i === 0 && r.generation != null ? ` generation ${r.generation}` : ''}
                      </span>
                    ))}
                  </span>
                </div>
              ))}
            </div>
            <div style={{ marginTop: 16 }}>
              {data.servers.map((row) => <ServerUpgrade key={row.id} row={row} />)}
            </div>
          </>
        )}
        <div className="page-note"><Link to="/instances">Back to the servers</Link></div>
      </div>
    </ConsoleShell>
  )
}
