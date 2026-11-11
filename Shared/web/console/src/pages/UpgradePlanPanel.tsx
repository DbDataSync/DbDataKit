import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { availableKey, type ServerAvailability } from '../upgradeApi'
import { planUpgrade, runUpgradePlan, type PlanEntry, type StepResult } from '../upgradePlan'
import { upgradeRunApi, type UpgradeStatus } from '../upgradeRun'

const ACTION_WORDS: Record<PlanEntry['action'], string> = {
  upgrade: 'will be upgraded',
  instruction: 'not upgraded by the console',
  manual: 'not upgraded from the console yet',
  blocked: 'waits for the console',
  current: 'up to date',
}

/**
 * Upgrade everything that has a newer release, one server at a time (plan 24a UC10): the plan in the order of QU4 (the serving layer, then the back-ends; the console first, by its own installer), a start, the progress of the
 * server being upgraded, and a stop at the first one that does not become healthy, with the servers after it named as left on the version they had. Each server upgrades itself (UC09), so every start is a call the audit records.
 */
export function UpgradePlanPanel({ servers }: { servers: ServerAvailability[] }) {
  const queryClient = useQueryClient()
  const [plan, setPlan] = useState<PlanEntry[] | null>(null)
  const [working, setWorking] = useState<'planning' | 'running' | null>(null)
  const [now, setNow] = useState<{ id: string; status: UpgradeStatus | null } | null>(null)
  const [results, setResults] = useState<StepResult[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const make = async () => {
    setWorking('planning'); setResults(null); setError(null)
    try { setPlan(await planUpgrade(servers, upgradeRunApi)) } catch (e) { setError(e instanceof Error ? e.message : String(e)) } finally { setWorking(null) }
  }
  const start = async () => {
    if (!plan) return
    setWorking('running'); setResults(null); setError(null)
    try {
      setResults(await runUpgradePlan(plan, upgradeRunApi, { onProgress: (id, status) => setNow({ id, status }) }))
      await queryClient.invalidateQueries({ queryKey: availableKey })
    } catch (e) { setError(e instanceof Error ? e.message : String(e)) } finally { setWorking(null); setNow(null) }
  }
  const toDo = plan?.filter((e) => e.action === 'upgrade').length ?? 0
  const failed = results?.find((r) => r.outcome === 'failed')

  return (
    <div className="card" style={{ marginBottom: 16 }} data-testid="upgrade-plan">
      <div className="card-head">
        <span className="card-title">Upgrade everything</span>
        <span className="hint">One server at a time: the serving layer, then the back-ends. It stops at the first server that does not become healthy.</span>
        <button type="button" className="btn" disabled={working !== null} onClick={() => void make()} data-testid="upgrade-plan-make">{working === 'planning' ? 'Planning…' : plan ? 'Plan again' : 'Plan'}</button>
      </div>
      <div className="card-body">
        {error && <div className="hint" data-tone="bad" data-testid="upgrade-plan-error">{error}</div>}
        {plan && (
          <>
            <ol data-testid="upgrade-plan-list" style={{ margin: 0, paddingLeft: 20 }}>
              {plan.map((e) => {
                const result = results?.find((r) => r.id === e.id)
                return (
                  <li key={e.id} data-testid={`upgrade-plan-entry-${e.name}`} data-action={e.action} data-result={result?.outcome ?? (now?.id === e.id ? 'running' : 'pending')}>
                    <strong>{e.name}</strong> <span className="mono hint">{e.product} {e.from}{e.action !== 'current' ? ` to ${e.to}` : ''}</span> · {ACTION_WORDS[e.action]}
                    {e.note && <div className="hint" data-testid={`upgrade-plan-note-${e.name}`}>{e.note}</div>}
                    {now?.id === e.id && <div className="hint" data-testid={`upgrade-plan-now-${e.name}`}>{now.status ? `${now.status.state.replace('_', ' ')}${now.status.state === 'draining' ? `, ${now.status.active_work} piece(s) of work still running` : ''}` : 'checking the release'}</div>}
                    {result?.outcome === 'committed' && <div data-testid={`upgrade-plan-done-${e.name}`} data-tone="ok">Upgraded{result.to ? ` to ${result.to}` : ''}.</div>}
                    {result?.outcome === 'failed' && <div data-testid={`upgrade-plan-failed-${e.name}`} data-tone="bad">{result.reason}</div>}
                    {result?.outcome === 'left' && <div className="hint" data-testid={`upgrade-plan-left-${e.name}`}>Not touched: it stays on {e.from}.</div>}
                  </li>
                )
              })}
            </ol>
            {toDo === 0 && <div className="hint" data-testid="upgrade-plan-nothing">Nothing for the console to upgrade now.</div>}
            {toDo > 0 && !results && (
              <button type="button" className="btn btn-primary" style={{ marginTop: 8 }} disabled={working !== null} onClick={() => void start()} data-testid="upgrade-plan-start">
                {working === 'running' ? 'Upgrading…' : `Upgrade ${toDo} server${toDo === 1 ? '' : 's'}, one at a time`}
              </button>
            )}
            {results && (
              <div data-testid="upgrade-plan-summary" data-stopped={failed ? 'true' : 'false'} style={{ marginTop: 8 }}>
                {failed
                  ? `Stopped at ${failed.name}: the servers after it stay on the version they had. Fix it, then plan again.`
                  : `Done: ${results.length} server${results.length === 1 ? '' : 's'} upgraded.`}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  )
}
