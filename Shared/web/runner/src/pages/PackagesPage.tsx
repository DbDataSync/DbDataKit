import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useParams } from 'react-router-dom'
import { ErrorBanner, Link, useIsAdmin } from '@dbdatakit/shell'
import { runnerApi, type ActivationAnswer, type ChannelHead, type DefinitionDetail, type StagingInfo } from '../api'
import { activationBody, headText, needsAnyway, stagingRows } from '../model'
import { RunnerShell } from '../RunnerShell'
import { when } from './format'
import { ConfirmDialog, Modal } from './Dialogs'
import { BindingsCard } from './BindingsCard'
import { PublishingSettings } from './PublishingSettings'
import { PublishFromFiles } from './PublishFromFiles'
import { PruneDialog } from './PruneDialog'

/**
 * The activation of a staged version. When the progress migration the runner ran as a dry run would lose something, the dialog lists the losses and the person confirms them: the runner takes the confirmation only with the same list echoed back. When
 * the runners are not all ready (one failed, or has not reported) an administrator can go ahead anyway, with a reason that is written to the audit.
 */
function ActivateDialog({ name, staging, active, onDone, onCancel }: { name: string; staging: StagingInfo; active: number | null; onDone: (answer: ActivationAnswer) => void; onCancel: () => void }) {
  const [confirmed, setConfirmed] = useState(false)
  const [anyway, setAnyway] = useState(false)
  const [reason, setReason] = useState('')
  const body = activationBody(staging, active, { confirmed, anyway, reason })
  const activate = useMutation({ mutationFn: () => runnerApi.activate(name, body!), onSuccess: onDone })

  return (
    <Modal label={`Activate version ${staging.version} on ${staging.channel}`} testId="runner-activate-dialog" onCancel={onCancel}>
      <span className="hint" data-testid="runner-activate-summary">{staging.summary}</span>
      {staging.needs_confirmation && (
        <div data-testid="runner-activate-losses">
          <p>This version loses progress that the tasks have made. Activating it discards:</p>
          <ul>{staging.losses.map((l) => <li key={l} className="mono" data-testid="runner-activate-loss">{l}</li>)}</ul>
          <label className="row" style={{ gap: 8 }}>
            <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} data-testid="runner-activate-confirm" />
            <span>I have read the list and accept the losses</span>
          </label>
        </div>
      )}
      {needsAnyway(staging) && (
        <div data-testid="runner-activate-anyway-box">
          <p>Not every runner has prepared this version{staging.failed.length > 0 ? `: ${staging.failed.map((f) => `${f.instance} (${f.error})`).join('; ')}` : ''}.</p>
          <label className="row" style={{ gap: 8 }}>
            <input type="checkbox" checked={anyway} onChange={(e) => setAnyway(e.target.checked)} data-testid="runner-activate-anyway" />
            <span>Activate anyway (administrators only; written to the audit)</span>
          </label>
          {anyway && <input className="input" placeholder="Why" value={reason} onChange={(e) => setReason(e.target.value)} data-testid="runner-activate-reason" />}
        </div>
      )}
      <ErrorBanner error={activate.error} />
      <div className="row" style={{ gap: 8, justifyContent: 'flex-end' }}>
        <button type="button" className="btn" onClick={onCancel} data-testid="runner-activate-cancel">Cancel</button>
        <button type="button" className="btn btn-primary" disabled={body === null || activate.isPending} onClick={() => activate.mutate()} data-testid="runner-activate-go">{activate.isPending ? 'Activating...' : 'Activate'}</button>
      </div>
    </Modal>
  )
}

type Pending =
  | { kind: 'activate'; staging: StagingInfo; active: number | null }
  | { kind: 'rollback' | 'retire'; head: ChannelHead }
  | { kind: 'switch'; head: ChannelHead; version: number }
  | { kind: 'prune' }

function PackageDetail({ detail, isAdmin }: { detail: DefinitionDetail; isAdmin: boolean }) {
  const client = useQueryClient()
  const [pending, setPending] = useState<Pending | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const refresh = () => client.invalidateQueries({ queryKey: ['runner'] })
  const done = (answer: unknown) => {
    setPending(null)
    const a = answer as Partial<ActivationAnswer> | undefined
    setMessage(a?.state === 'waiting' || (a?.busy_tasks?.length ?? 0) > 0 ? `${a?.message ?? 'Waiting for the runs in flight.'} (${a?.busy_tasks?.join(', ')})` : (a?.message ?? null))
    void refresh()
  }
  const expected = (head: ChannelHead) => ({ channel: head.channel, expected_version: head.active ?? 0 })

  return (
    <div data-testid="runner-package" data-package={detail.name}>
      {message && <div className="empty" data-testid="runner-package-message">{message}</div>}
      {detail.channels.map((head) => {
        const staging = detail.staging.find((s) => s.channel === head.channel)
        const reports = staging ? stagingRows(staging, detail.instances) : []
        return (
          <div key={head.channel} className="card" data-testid="runner-channel" data-channel={head.channel}>
            <div className="card-body">
              <div className="row-between">
                <div className="card-title">{head.channel}: <span data-testid="runner-channel-heads">{headText(head)}</span></div>
                {isAdmin && (
                  <span className="row" style={{ gap: 8 }}>
                    {head.previous !== null && <button type="button" className="btn btn-sm" onClick={() => setPending({ kind: 'rollback', head })} data-testid="runner-rollback">Roll back</button>}
                    {head.active !== null && <button type="button" className="btn btn-sm" onClick={() => setPending({ kind: 'retire', head })} data-testid="runner-retire">Retire</button>}
                  </span>
                )}
              </div>
              <BindingsCard detail={detail} channel={head.channel} canEdit={isAdmin} />
              {staging && (
                <div data-testid="runner-staging" data-version={staging.version} data-needs-confirmation={staging.needs_confirmation}>
                  <p>Version {staging.version} is staged: <span data-testid="runner-staging-summary">{staging.summary}</span></p>
                  <div className="card flush">
                    {reports.length === 0 && <div className="empty">No runner has reported on it yet.</div>}
                    {reports.map((r) => (
                      <div key={r.instance} className="grid-row" style={{ gridTemplateColumns: '2fr 1fr 3fr', gap: 14 }} data-testid="runner-staging-instance" data-state={r.state}>
                        <span className="mono">{r.instance}{r.gone ? ' (gone)' : ''}</span>
                        <span data-testid="runner-staging-state">{r.state}</span>
                        <span>{r.error ?? r.note ?? ''}</span>
                      </div>
                    ))}
                  </div>
                  {staging.needs_confirmation && (
                    <div data-testid="runner-staging-losses">
                      <p>Activating it loses progress:</p>
                      <ul>{staging.losses.map((l) => <li key={l} className="mono">{l}</li>)}</ul>
                    </div>
                  )}
                  {isAdmin && <button type="button" className="btn btn-primary" onClick={() => setPending({ kind: 'activate', staging, active: head.active })} data-testid="runner-activate">Activate version {staging.version}</button>}
                </div>
              )}
            </div>
          </div>
        )
      })}

      <div className="page-head" style={{ marginTop: 24 }}><h2 className="page-title">Versions</h2>{isAdmin && <button type="button" className="btn btn-sm" onClick={() => setPending({ kind: 'prune' })} data-testid="runner-prune">Prune versions</button>}</div>
      <div className="card flush" data-testid="runner-versions">
        <div className="grid-head" style={{ gridTemplateColumns: '0.6fr 1.6fr 1.4fr 3fr 1.2fr', gap: 14 }}><span>Version</span><span>Published</span><span>By</span><span>Note</span><span /></div>
        {detail.versions.map((v) => (
          <div key={v.version} className="grid-row" style={{ gridTemplateColumns: '0.6fr 1.6fr 1.4fr 3fr 1.2fr', gap: 14 }} data-testid="runner-version" data-version={v.version}>
            <span>v{v.version}</span><span>{when(v.published_at)}</span><span>{v.published_by}</span><span>{v.note ?? ''} ({v.files} files)</span>
            <span className="row" style={{ gap: 6 }}>
              {isAdmin && detail.channels.filter((h) => h.active !== v.version).map((head) => (
                <button key={head.channel} type="button" className="btn btn-sm" onClick={() => setPending({ kind: 'switch', head, version: v.version })} data-testid="runner-switch">Serve on {head.channel}</button>
              ))}
            </span>
          </div>
        ))}
      </div>

      <div className="page-head" style={{ marginTop: 24 }}><h2 className="page-title">History</h2></div>
      <div className="card flush" data-testid="runner-events">
        {detail.events.slice(0, 30).map((e) => (
          <div key={e.id} className="grid-row" style={{ gridTemplateColumns: '1.6fr 1fr 0.8fr 0.6fr 1.4fr 2fr', gap: 14 }} data-testid="runner-event">
            <span>{when(e.at)}</span><span>{e.action}</span><span>{e.channel ?? ''}</span><span>{e.version === null ? '' : `v${e.version}`}</span><span>{e.actor ?? ''}</span><span>{e.note ?? ''}</span>
          </div>
        ))}
      </div>

      {pending?.kind === 'activate' && <ActivateDialog name={detail.name} staging={pending.staging} active={pending.active} onDone={done} onCancel={() => setPending(null)} />}
      {pending?.kind === 'rollback' && <ConfirmDialog label="Roll back" text={`Serve version ${pending.head.previous} again on ${pending.head.channel}; the progress is put back as it was.`} go={() => runnerApi.rollback(detail.name, expected(pending.head))} onCancel={() => setPending(null)} onDone={done} />}
      {pending?.kind === 'retire' && <ConfirmDialog label="Retire" text={`Stop serving ${detail.name} on ${pending.head.channel}: its tasks leave the schedule.`} go={() => runnerApi.retire(detail.name, expected(pending.head))} onCancel={() => setPending(null)} onDone={done} />}
      {pending?.kind === 'prune' && <PruneDialog name={detail.name} versions={detail.versions.map((v) => v.version)} heads={detail.channels} onCancel={() => setPending(null)} onDone={done} />}
      {pending?.kind === 'switch' && <ConfirmDialog label="Serve" text={`Serve version ${pending.version} on ${pending.head.channel}.`} go={() => runnerApi.switchTo(detail.name, { ...expected(pending.head), version: pending.version })} onCancel={() => setPending(null)} onDone={done} />}
    </div>
  )
}

/** The packages the runner holds: each with what every channel serves, and, for one, its versions, how far the runners are with a staged version, and the activation with its confirmation. */
export function PackagesPage() {
  const { name } = useParams()
  const isAdmin = useIsAdmin()
  const list = useQuery({ queryKey: ['runner', 'definitions'], queryFn: runnerApi.definitions, refetchInterval: 5000 })
  const detail = useQuery({ queryKey: ['runner', 'definition', name], queryFn: () => runnerApi.definition(name!), enabled: !!name, refetchInterval: 3000 })

  return (
    <RunnerShell crumbs={name ? [{ label: 'Packages', to: '/packages' }, { label: name, heading: true }] : [{ label: 'Packages', heading: true }]}>
      <div className="pane" data-testid="runner-packages-page">
        <div className="page-head"><h1 className="page-title">{name ?? 'Packages'}</h1></div>
        <ErrorBanner error={list.error ?? detail.error} />
        {!name && <PublishingSettings canEdit={isAdmin} />}
        {!name && <PublishFromFiles canPublish={isAdmin} />}
        {!name && list.isSuccess && list.data.length === 0 && <div className="empty" data-testid="runner-no-packages">No package has been published to this runner.</div>}
        {!name && (list.data ?? []).map((p) => (
          <div key={p.name} className="card" data-testid="runner-package-row">
            <div className="card-body">
              <div className="card-title"><Link to={`/packages/${encodeURIComponent(p.name)}`} data-testid="runner-package-link">{p.name}</Link></div>
              <div className="hint">{p.channels.map((c) => `${c.channel}: ${headText(c)}`).join('; ')}{p.owner ? `, owned by ${p.owner}` : ''}</div>
            </div>
          </div>
        ))}
        {name && detail.data && <PackageDetail detail={detail.data} isAdmin={isAdmin} />}
      </div>
    </RunnerShell>
  )
}
