import { useEffect, useRef, useState } from 'react'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { AppShell } from '../components/AppShell'
import { useBindRunnerSlots, useCheckRunnerPackage, usePublishRunnerPackage, useRunnerLink, useRunnerPackage, useUnpublishedChanges } from '../api/hooks'
import type { ReplicationSetPublication, RunnerPackageView } from '../api/types'
import { RunnerLinkCard } from './RunnerLinkCard'
import { bindingsToSend, packageState, scheduleText, slotChoices, type SlotChoice } from './runnerPublish'

/** Where the package stands on the runner: the version the channel serves, the one staged behind it and how far the runners are, and what each runner reports. */
export function RunnerPackageCard({ view }: { view: RunnerPackageView | undefined }) {
  const state = packageState(view)
  return (
    <div className="card" data-testid="runner-package">
      <div className="card-head tight">
        <span className="card-title sm">Published to the runner</span>
        <span className="badge spacer" data-testid="runner-package-state" data-state={state.key}>{state.text}</span>
      </div>
      {view?.linked && view.published && (
        <dl style={{ display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '4px 16px', margin: 0 }}>
          <dt>Package</dt><dd className="mono" data-testid="runner-package-name">{view.package} on {view.server}, channel {view.channel}</dd>
          <dt>Served version</dt><dd data-testid="runner-package-active">{view.active ?? 'none'}{view.previous != null ? ` (the one before: ${view.previous})` : ''}</dd>
          <dt>Staged version</dt>
          <dd data-testid="runner-package-staged">
            {view.staged == null ? 'none' : `${view.staged}: ${view.staging?.allReady ? 'ready on every runner' : view.staging?.summary ?? 'being prepared'}`}
          </dd>
          <dt>Latest published</dt><dd>{view.latest ?? 'none'}</dd>
        </dl>
      )}
      {view?.linked && !view.published && <p className="hint" data-testid="runner-package-none">The runner has no package called {view.package}: publish the replication set below.</p>}
      {!view?.linked && <p className="hint" data-testid="runner-package-unlinked">Set the runner (server, channel and package) to publish the replications to it.</p>}
      {view?.staging && view.staging.failed.length > 0 && (
        <div className="banner banner-error" role="alert" data-testid="runner-staging-failed">
          {view.staging.failed.map((f) => <div key={f}>{f}</div>)}
        </div>
      )}
      {view?.staging?.needsConfirmation && (
        <p className="hint" data-testid="runner-staging-losses">Activating version {view.staging.version} would lose progress the runner holds: {view.staging.losses.join('; ')}. An administrator confirms it on the runner&apos;s Packages screen.</p>
      )}
      {view && view.instances.length > 0 && (
        <table className="table" style={{ marginTop: 12 }} data-testid="runner-instances">
          <thead><tr><th>Runner</th><th>Version</th><th>State</th><th>Said</th></tr></thead>
          <tbody>
            {view.instances.map((i) => (
              <tr key={`${i.instance}-${i.version}`} data-testid={`runner-instance-${i.instance}`}>
                <td className="mono">{i.instance}{i.gone ? ' (gone)' : ''}</td><td>{i.version}</td><td>{i.state}</td><td>{i.error ?? i.note ?? ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {view && view.events.length > 0 && (
        <details style={{ marginTop: 12 }}>
          <summary>History on the runner</summary>
          <ul data-testid="runner-events">
            {[...view.events].reverse().map((e, n) => <li key={n}>{e.action}{e.version != null ? ` version ${e.version}` : ''}{e.actor ? ` by ${e.actor}` : ''}{e.at ? `, ${new Date(e.at).toLocaleString()}` : ''}</li>)}
          </ul>
        </details>
      )}
    </div>
  )
}

/** The replications a publish would send, one row each: the task they become, how they are scheduled there, the slots they need. */
export function PublishedReplicationsTable({ check }: { check: ReplicationSetPublication }) {
  return (
    <table className="table" data-testid="runner-check-table">
      <thead><tr><th>Replication</th><th>Task</th><th>Schedule on the runner</th><th>Mappings</th><th>Slots</th></tr></thead>
      <tbody>
        {check.replications.map((r) => (
          <tr key={r.replication} data-testid={`runner-check-row-${r.replication}`}>
            <td>{r.replication}</td><td className="mono">{r.task}</td><td>{scheduleText(r)}</td><td>{r.mappings}</td><td className="mono">{r.slots.join(', ')}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

/** The slots a publish declares and what each is to be bound to on the runner (a name of one of the runner's connections, never a value). */
export function SlotBindings({ choices, edits, onEdit, disabled }: { choices: SlotChoice[]; edits: Record<string, string>; onEdit: (slot: string, value: string) => void; disabled: boolean }) {
  if (choices.length === 0) return null
  return (
    <table className="table" data-testid="runner-slots">
      <thead><tr><th>Slot</th><th>For the connection</th><th>Bound on the runner to</th></tr></thead>
      <tbody>
        {choices.map((c) => (
          <tr key={c.slot} data-testid={`runner-slot-${c.slot}`}>
            <td className="mono">{c.slot}</td>
            <td>{c.connections.join(', ') || ''}</td>
            <td>
              <input className="input" aria-label={`The runner's connection bound to ${c.slot}`} value={edits[c.slot] ?? c.value} disabled={disabled} onChange={(e) => onEdit(c.slot, e.target.value)} data-testid={`runner-slot-input-${c.slot}`} />
              {c.boundTo == null && <span className="hint" data-testid={`runner-slot-unbound-${c.slot}`}> not bound yet: the runner will not activate a version that declares it</span>}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

/**
 * The Runner screen (plan 28c E08): the replication set on the paired runner. It shows what a publish would hold or why it would be refused (in the words of the verb `dbdatasync package publish`, which does the work), publishes it staged
 * (the runners prepare the version and one activates it once every runner has it ready) or served at once, binds the slots to the runner's connections, and shows the package as the runner holds it. Publishing and binding need the admin role.
 */
export function RunnerPage() {
  const isAdmin = useIsAdmin()
  const link = useRunnerLink()
  const pkg = useRunnerPackage()
  const unpublished = useUnpublishedChanges()
  const check = useCheckRunnerPackage()
  const publish = usePublishRunnerPackage()
  const bind = useBindRunnerSlots()
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [done, setDone] = useState<ReplicationSetPublication | null>(null)

  // what a publish would hold is asked for once when a runner is set, and again on request or after a publish
  const asked = useRef(false)
  useEffect(() => {
    if (link.data && !asked.current) {
      asked.current = true
      check.mutate()
    }
  }, [link.data, check])

  const choices = slotChoices(check.data, pkg.data, edits)
  const send = bindingsToSend(choices)
  const linked = !!link.data
  const busy = publish.isPending || check.isPending
  const reason = !isAdmin ? 'Publishing needs the admin role.' : !linked ? 'Set the runner first.' : check.error ? 'Fix what the check names first.' : busy ? 'A publish or a check is running.' : undefined

  const go = async (mode: 'stage' | 'serve') => {
    setDone(null)
    try {
      const result = await publish.mutateAsync({ mode, bind: Object.keys(send).length > 0 ? send : undefined })
      setDone(result)
      setEdits({})
    } catch { /* shown below, in the words of whoever refused */ }
  }

  return (
    <AppShell crumbs={[{ label: 'Runner' }]}>
      <div className="pane" data-testid="runner-page">
        <div className="page-head">
          <h1 className="page-title">Runner</h1>
          <span className="page-note">the replication set on the paired runner</span>
        </div>
        {unpublished.data?.everPublished && unpublished.data.unpublished && (
          <div className="banner warn" role="status" data-testid="runner-unpublished-changes">
            <strong>Unpublished changes.</strong>{' '}
            {unpublished.data.replications.length > 0 && <>Changed since version {unpublished.data.version ?? '?'} was published: {unpublished.data.replications.join(', ')}. </>}
            {unpublished.data.sharedChanged && <>Connections, libraries, drivers or files, which every replication uses, changed. </>}
            A runner runs the published version until the replication set is published again.
          </div>
        )}

        <RunnerPackageCard view={pkg.data} />
        {pkg.error && <ErrorBanner error={pkg.error} />}

        <div className="card" data-testid="runner-publish">
          <div className="card-head tight"><span className="card-title sm">Publish the replications</span></div>
          <p className="hint">
            Every enabled replication becomes a task on the runner, with the definition, its schedule and a connection slot for each connection it reads or writes. No secret goes: a slot is bound to a connection of the runner, which holds its own login.
            Staged, the runners prepare the version and one of them makes it the served one once every runner has it ready; served, it is the served version at once.
          </p>
          {check.isPending && <div className="hint" data-testid="runner-check-running">Reading the definition…</div>}
          {check.data && <PublishedReplicationsTable check={check.data} />}
          {check.error && <div data-testid="runner-check-refused"><ErrorBanner error={check.error} /></div>}
          <SlotBindings choices={choices} edits={edits} onEdit={(slot, value) => setEdits({ ...edits, [slot]: value })} disabled={!isAdmin || busy} />
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 12 }}>
            <button type="button" className="btn btn-sm btn-primary" disabled={!!reason} title={reason} onClick={() => go('stage')} data-testid="runner-publish-stage">Stage on the runner</button>
            <button type="button" className="btn btn-sm" disabled={!!reason} title={reason} onClick={() => go('serve')} data-testid="runner-publish-serve">Publish and serve at once</button>
            <button type="button" className="btn btn-sm" disabled={!linked || busy} onClick={() => check.mutate()} data-testid="runner-check">Check again</button>
          </div>
          {publish.isPending && <div className="hint" data-testid="runner-publishing">Publishing: restoring the libraries for the runner and sending the files it does not have…</div>}
          {done && (
            <div className="hint" data-testid="runner-publish-done">
              {done.mode === 'stage' ? 'Staged' : 'Published'} {done.package} on {done.channel}: version {done.version}{done.changed === false ? ' (the same content as before, nothing changed)' : ''}, {done.uploaded} file(s) sent, {done.skipped} already there.
              {done.bound && done.bound.length > 0 ? ` Bound ${done.bound.join(', ')}.` : ''}
            </div>
          )}
          {publish.error && <div data-testid="runner-publish-refused"><ErrorBanner error={publish.error} /></div>}
        </div>

        <RunnerSlotsCard view={pkg.data} isAdmin={isAdmin} bind={bind} />
        <RunnerLinkCard />
      </div>
    </AppShell>
  )
}

/** The slots the manifests on the runner declare in this channel and what each is bound to; binding is an administrator's call and does not publish anything. */
function RunnerSlotsCard({ view, isAdmin, bind }: { view: RunnerPackageView | undefined; isAdmin: boolean; bind: ReturnType<typeof useBindRunnerSlots> }) {
  const [edits, setEdits] = useState<Record<string, string>>({})
  if (!view?.linked || !view.published || view.slots.length === 0) return null
  const choices = slotChoices(undefined, view, edits)
  const send = bindingsToSend(choices)
  const unbound = view.slots.filter((s) => s.boundTo == null)
  return (
    <div className="card" data-testid="runner-slots-card">
      <div className="card-head tight">
        <span className="card-title sm">Connection slots on the runner</span>
        <span className="badge spacer" data-testid="runner-slots-state">{unbound.length === 0 ? 'all bound' : `${unbound.length} not bound`}</span>
      </div>
      <p className="hint">The slots the versions in {view.channel} declare. A version that declares a slot nothing is bound to is refused when it is activated, naming the slot and the channel.</p>
      <table className="table">
        <thead><tr><th>Slot</th><th>Kind</th><th>Tasks</th><th>Versions</th><th>Bound to</th></tr></thead>
        <tbody>
          {view.slots.map((s) => (
            <tr key={s.slot} data-testid={`runner-slots-row-${s.slot}`}>
              <td className="mono">{s.slot}</td><td>{s.kind}</td><td className="mono">{s.tasks.join(', ')}</td><td>{s.versions.join(', ')}</td>
              <td>
                <input className="input" aria-label={`The runner's connection bound to ${s.slot}`} value={edits[s.slot] ?? s.boundTo ?? ''} disabled={!isAdmin} onChange={(e) => setEdits({ ...edits, [s.slot]: e.target.value })} data-testid={`runner-slots-input-${s.slot}`} />
                {s.boundTo == null && <span className="hint" data-testid={`runner-slots-unbound-${s.slot}`}> not bound</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div style={{ marginTop: 12 }}>
        <button type="button" className="btn btn-sm" disabled={!isAdmin || Object.keys(send).length === 0 || bind.isPending} title={!isAdmin ? 'Binding needs the admin role.' : Object.keys(send).length === 0 ? 'Nothing is changed.' : undefined}
          onClick={() => bind.mutate(send, { onSuccess: () => setEdits({}) })} data-testid="runner-slots-bind">Bind</button>
      </div>
      {bind.error && <div data-testid="runner-slots-refused"><ErrorBanner error={bind.error} /></div>}
    </div>
  )
}
