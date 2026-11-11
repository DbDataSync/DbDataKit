import type { ServerAvailability } from './upgradeApi'
import { isFinished, offersUpgrade, type PrepareReport, type UpgradeStatus } from './upgradeRun'

/**
 * Upgrading every server that has a newer release, one at a time (plan 24a UC10, QU4): the order is **the console first** (it is upgraded by its own installer, so a server whose release would leave it outside N and N-1 of this
 * console waits for that and is marked), **then the serving layer, then the back-ends**; inside a group by name. The console only orders and drives: each server upgrades itself through its own call (UC09), relayed as the signed-in
 * administrator, so every start is a write the audit already records against the server. A run **stops at the first server that does not become healthy**; the servers after it stay on the version they had.
 */
export type PlanAction =
  | 'upgrade'       // will be upgraded in this run
  | 'instruction'   // an image or a package install: the console shows the command and does not upgrade it (QU3)
  | 'manual'        // a kind of server the console does not upgrade yet
  | 'blocked'       // the console must be upgraded first (the release is outside N and N-1 of it)
  | 'current'       // nothing newer

export interface PlanEntry {
  id: string
  name: string
  product: string | null
  from: string | null
  to: string | null
  group: 'serving' | 'back-end'
  action: PlanAction
  /** Why the server is not in the run, or the command to run for an instruction. */
  note: string | null
}

/** The serving layer is the Gateway; every other product is a back-end. */
export const groupOf = (product: string | null): 'serving' | 'back-end' => (product === 'dbdatagateway' ? 'serving' : 'back-end')

export interface PlanApi {
  status: (id: string) => Promise<UpgradeStatus>
  prepare: (id: string, version?: string) => Promise<PrepareReport>
  apply: (id: string) => Promise<UpgradeStatus>
}

/** The plan for the servers the feed says have a newer release. `status` of each server decides whether it is upgraded by the console (an image or a package install is not). */
export async function planUpgrade(servers: readonly ServerAvailability[], api: Pick<PlanApi, 'status'>): Promise<PlanEntry[]> {
  const entries: PlanEntry[] = []
  for (const s of servers) {
    const base = { id: s.id, name: s.name, product: s.product, from: s.version, to: s.newest, group: groupOf(s.product) }
    if (!s.newerAvailable) { entries.push({ ...base, action: 'current', note: null }); continue }
    if (s.outsideAfterRelease) { entries.push({ ...base, action: 'blocked', note: `Upgrade the console first: ${s.outsideAfterRelease}` }); continue }
    if (!offersUpgrade(s.product)) { entries.push({ ...base, action: 'manual', note: 'This kind of server is not upgraded from the console yet: use its install script or installer.' }); continue }
    try {
      const status = await api.status(s.id)
      entries.push(status.can_apply ? { ...base, action: 'upgrade', note: null } : { ...base, action: 'instruction', note: status.reason ?? 'The console does not upgrade this install.' })
    } catch (e) {
      entries.push({ ...base, action: 'manual', note: `The server did not say how it is installed: ${e instanceof Error ? e.message : String(e)}` })
    }
  }
  return entries.sort((a, b) => order(a) - order(b) || a.name.localeCompare(b.name))
}

const order = (e: PlanEntry) => (e.group === 'serving' ? 0 : 1)

/** What happened to one server of a run. `left` is a server after the one that failed: it was not touched. */
export type StepResult =
  | { id: string; name: string; outcome: 'committed'; to: string | null }
  | { id: string; name: string; outcome: 'failed'; reason: string }
  | { id: string; name: string; outcome: 'left' }

export interface RunOptions {
  /** Called as the run moves, for the screen: which server is being upgraded now, and the last status it gave. */
  onProgress?: (id: string, status: UpgradeStatus | null) => void
  /** How long to wait between asking a server how it is; a server that is being swapped does not answer, which is not a failure. */
  sleep?: (ms: number) => Promise<void>
  pollMs?: number
  /** How many asks in a row may fail (the server restarting) before the run gives up on it. */
  maxSilent?: number
}

/** Runs the plan's `upgrade` entries in order; stops at the first that is refused, rolled back or failed. Entries that are not `upgrade` are not touched. */
export async function runUpgradePlan(plan: readonly PlanEntry[], api: PlanApi, options: RunOptions = {}): Promise<StepResult[]> {
  const sleep = options.sleep ?? ((ms: number) => new Promise<void>((r) => setTimeout(r, ms)))
  const pollMs = options.pollMs ?? 2000
  const maxSilent = options.maxSilent ?? 150
  const results: StepResult[] = []
  const todo = plan.filter((e) => e.action === 'upgrade')
  for (let i = 0; i < todo.length; i++) {
    const entry = todo[i]
    const fail = (reason: string) => {
      results.push({ id: entry.id, name: entry.name, outcome: 'failed', reason })
      for (const rest of todo.slice(i + 1)) results.push({ id: rest.id, name: rest.name, outcome: 'left' })
    }
    options.onProgress?.(entry.id, null)
    let report: PrepareReport
    try { report = await api.prepare(entry.id) } catch (e) { fail(`Check failed: ${e instanceof Error ? e.message : String(e)}`); return results }
    if (report.state !== 'prepared' || !report.can_apply) { fail(report.message || 'The server did not prepare the upgrade.'); return results }
    let status: UpgradeStatus
    try { status = await api.apply(entry.id) } catch (e) { fail(`The upgrade did not start: ${e instanceof Error ? e.message : String(e)}`); return results }
    options.onProgress?.(entry.id, status)
    let silent = 0
    while (!isFinished(status.state)) {
      await sleep(pollMs)
      try { status = await api.status(entry.id); silent = 0; options.onProgress?.(entry.id, status) } catch {
        if (++silent > maxSilent) { fail('The server did not answer again after it was restarted.'); return results }
      }
    }
    if (status.state !== 'committed') { fail(status.message ?? status.plan?.reason ?? `The upgrade ended ${status.state.replace('_', ' ')}.`); return results }
    results.push({ id: entry.id, name: entry.name, outcome: 'committed', to: status.plan?.to ?? entry.to })
  }
  return results
}
