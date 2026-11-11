import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import { consoleApi } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { counts, findings, loadSecrets, type SecretFinding } from '../secrets'

const kindText: Record<SecretFinding['kind'], string> = { missing: 'Missing', unused: 'Unused', outside: 'Outside' }

/**
 * Which references the definitions of every Gateway and runner name that no secret answers (missing: a data source or a package that will fail to connect), which secrets nothing names (unused: left over), and which references point
 * outside the server's own store (the environment, a file: the server cannot say whether they answer). Names, references and dates: a value is never read, nor shown. Read only: a secret is written in the owning product's screen, which each row opens.
 */
export function SecretsPage() {
  const instances = useQuery({ queryKey: ['instances'], queryFn: consoleApi.instances.list })
  const servers = instances.data ?? []
  const view = useQuery({
    queryKey: ['secrets-overview', servers.map((s) => `${s.id}:${s.reachable}`).join(',')],
    queryFn: () => loadSecrets(servers),
    enabled: instances.isSuccess,
    refetchInterval: 60_000,
    refetchOnMount: 'always',
  })
  const all = view.data ?? []
  const rows = findings(all)
  const n = counts(rows)
  const unanswered = all.filter((s) => !s.overview)

  return (
    <ConsoleShell crumbs={[{ label: 'Secrets', heading: true }]}>
      <div className="pane" data-testid="secrets-page">
        <div className="page-head">
          <h1 className="page-title">Secrets</h1>
          <span className="page-note">What the connections of your Gateways and runners name, against the secrets each keeps. Values are never shown.</span>
        </div>
        <ErrorBanner error={instances.error ?? view.error} />
        {unanswered.map((s) => (
          <div key={s.server} className="empty" data-testid="secrets-problem">{`${s.serverName}: no overview (${s.problem}).`}</div>
        ))}
        {view.isLoading && <div className="hint" data-testid="secrets-loading">Asking the servers…</div>}
        {view.isSuccess && all.length === 0 && <div className="empty" data-testid="secrets-none">No Gateway or runner answers.</div>}
        {view.isSuccess && all.length > 0 && <div className="hint" data-testid="secrets-summary">{n.missing} missing, {n.unused} unused, {n.outside} outside the server's store.</div>}
        {view.isSuccess && all.some((s) => s.overview) && rows.length === 0 && <div className="empty" data-testid="secrets-clean">Every reference a definition names has its secret, and every secret is named.</div>}
        {rows.length > 0 && (
          <div className="card flush" data-testid="secrets-rows" role="table" aria-label="Secrets that need a look">
            <div className="grid-head" role="row" style={{ gridTemplateColumns: '0.8fr 1.2fr 3fr 3fr 0.8fr', gap: 14 }}><span>Finding</span><span>Server</span><span>Reference or secret</span><span>Detail</span><span /></div>
            {rows.map((row) => (
              <div key={row.key} role="row" className="grid-row" style={{ gridTemplateColumns: '0.8fr 1.2fr 3fr 3fr 0.8fr', gap: 14 }} data-testid="secrets-row" data-kind={row.kind}>
                <span data-testid="secrets-kind">{kindText[row.kind]}</span>
                <span>{row.serverName}</span>
                <span className="mono">{row.subject}</span>
                <span>{row.detail}</span>
                <Link className="btn" to={row.link} data-testid="secrets-open">Open</Link>
              </div>
            ))}
          </div>
        )}
      </div>
    </ConsoleShell>
  )
}
