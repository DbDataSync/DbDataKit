import { useEffect, useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { proxyApi } from '../api'
import {
  approveBlock, NEEDS_PUBLISHER, removeAppBlock, removeInstanceBlock, revokeBlock, runsLabel, safeAddress, serveBlock, shortHash, sortApprovals, sortApps, sortInstances, trustLabel,
} from '../apps'
import { ProxyShell } from '../ProxyShell'
import type { AppAbout, AppApprovalRow, AppDetail, AppInstanceRow, AppsCaller, AppSummary, AppVersionRow, NavigationSetRow } from '../types'

const when = (iso: string) => new Date(iso).toLocaleString()

/** The grid rows clip long text; what a person must read (a reason, an address, an id) wraps instead. */
const wrap = { whiteSpace: 'normal', overflowWrap: 'anywhere' } as const

/** A button that is off says why, next to itself and as its tooltip, so a person is not left guessing which role or step is missing. */
function Why({ reason, testId }: { reason: string | null; testId: string }) {
  return reason ? <div className="hint" style={wrap} data-testid={testId}>{reason}</div> : null
}

function Section({ title, note, testId, children }: { title: string; note?: string; testId: string; children: ReactNode }) {
  return (
    <section style={{ marginTop: 24 }} data-testid={testId}>
      <h2 className="card-title">{title}</h2>
      {note && <p className="hint" style={{ margin: '2px 0 8px' }}>{note}</p>}
      {children}
    </section>
  )
}

// ---- instances ----

export interface InstancesViewProps {
  rows: AppInstanceRow[]
  /** The versions of each app, for the pin chooser. */
  versionsOf: (app: string) => number[]
  caller: AppsCaller
  appsAddress: string | null
  /** The pin chosen in each row's list, by instance name: a number, or '' for "follow". Absent means unchanged. */
  chosen: Record<string, string>
  /** The instance whose removal waits for a second click. */
  confirming: string | null
  busy: boolean
  onChoose: (name: string, value: string) => void
  onPin: (row: AppInstanceRow) => void
  onAskRemove: (name: string | null) => void
  onRemove: (name: string) => void
}

const INSTANCE_COLUMNS = '1.1fr 1.1fr 2.2fr 1.6fr 1.5fr 2.4fr'

/** The instances: which app, which version each runs (follow or pin), whether it runs as its manifest asks, where it is served, and the actions. */
export function InstancesView(p: InstancesViewProps) {
  return (
    <div className="card flush" data-testid="instances-table">
      <div className="grid-head" style={{ gridTemplateColumns: INSTANCE_COLUMNS, gap: 14 }}><span>Instance</span><span>App</span><span>Runs</span><span>Trust</span><span>Address</span><span>Actions</span></div>
      {p.rows.length === 0 && <div className="empty" data-testid="instances-empty">No instance yet. An instance is made with <span className="mono">apps instance-save</span>.</div>}
      {p.rows.map((i) => {
        const address = safeAddress(i.address)
        const wanted = p.chosen[i.name] ?? (i.pin === null ? '' : String(i.pin))
        const changed = wanted !== (i.pin === null ? '' : String(i.pin))
        const pinReason = p.caller.can_publish ? null : NEEDS_PUBLISHER
        const removeReason = removeInstanceBlock(p.caller, i)
        return (
          <div key={i.name} className="grid-row auto" style={{ gridTemplateColumns: INSTANCE_COLUMNS, gap: 14, alignItems: 'start' }} data-testid={`instance-row-${i.name}`}>
            <span><span className="mono" data-testid="instance-name">{i.name}</span>{i.title && <div className="hint">{i.title}</div>}</span>
            <span><span className="mono" data-testid="instance-app">{i.app}</span>{i.kind === 'portal' && <div className="hint" data-testid="instance-portal">portal{i.mounts?.length ? `, mounts ${i.mounts.map((m) => m.instance).join(', ')}` : ''}</div>}</span>
            <span style={wrap}>
              <span data-testid="instance-runs">{runsLabel(i)}</span>
              {i.problem && <div className="hint" role="alert" style={wrap} data-testid="instance-problem">{i.problem}</div>}
              {i.manifest_version && <div className="hint">{i.manifest_version}{i.navigation.length > 0 ? `, in the menu ${i.navigation.join(', ')}` : ''}</div>}
            </span>
            <span style={wrap} data-testid="instance-trust">{i.problem ? '—' : trustLabel(i)}</span>
            <span>
              {address
                ? <a href={address} target="_blank" rel="noopener noreferrer" data-testid={`instance-open-${i.name}`}>Open</a>
                : <span className="hint" style={wrap} data-testid={`instance-nohost-${i.name}`}>{p.appsAddress ? i.path : `${i.path} (this Proxy serves no apps host)`}</span>}
              {address && <div className="hint mono" style={wrap} data-testid="instance-address">{address}</div>}
            </span>
            <span>
              <span className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
                <select className="input sm" style={{ width: 190, flex: 'none' }} value={wanted} disabled={!p.caller.can_publish} onChange={(e) => p.onChoose(i.name, e.target.value)} data-testid={`instance-pin-${i.name}`} aria-label={`Version of ${i.name}`}>
                  <option value="">Follow the app</option>
                  {p.versionsOf(i.app).map((v) => <option key={v} value={String(v)}>Pin to version {v}</option>)}
                </select>
                <button type="button" className="btn sm" disabled={!p.caller.can_publish || !changed || p.busy} onClick={() => p.onPin(i)} data-testid={`instance-pin-save-${i.name}`}>Apply</button>
              </span>
              <Why reason={pinReason} testId={`instance-pin-why-${i.name}`} />
              <div className="row" style={{ gap: 6, marginTop: 6, flexWrap: 'wrap' }}>
                {p.confirming === i.name
                  ? (
                    <>
                      <button type="button" className="btn sm btn-primary" disabled={p.busy} onClick={() => p.onRemove(i.name)} data-testid={`instance-remove-confirm-${i.name}`}>Stop serving {i.name}</button>
                      <button type="button" className="btn sm" onClick={() => p.onAskRemove(null)}>Not now</button>
                    </>
                  )
                  : <button type="button" className="btn sm" disabled={removeReason !== null || p.busy} title={removeReason ?? undefined} onClick={() => p.onAskRemove(i.name)} data-testid={`instance-remove-${i.name}`}>Remove</button>}
              </div>
              <Why reason={removeReason} testId={`instance-remove-why-${i.name}`} />
            </span>
          </div>
        )
      })}
    </div>
  )
}

// ---- apps and their versions ----

export interface AppsViewProps {
  apps: AppSummary[]
  selected: string | null
  caller: AppsCaller
  confirming: string | null
  busy: boolean
  onSelect: (name: string | null) => void
  onAskRemove: (name: string | null) => void
  onRemove: (name: string) => void
}

const APP_COLUMNS = '1.3fr 1fr 0.9fr 1fr 1.6fr 2fr'

/** The published apps: the version each serves, who published it, its instances, and removal. */
export function AppsView(p: AppsViewProps) {
  return (
    <div className="card flush" data-testid="apps-table">
      <div className="grid-head" style={{ gridTemplateColumns: APP_COLUMNS, gap: 14 }}><span>App</span><span>Serves</span><span>Trust</span><span>Versions</span><span>Instances</span><span>Actions</span></div>
      {p.apps.length === 0 && <div className="empty" data-testid="apps-empty">No app is published. Publish one with <span className="mono">apps publish</span>.</div>}
      {p.apps.map((a) => {
        const reason = removeAppBlock(p.caller, a)
        return (
          <div key={a.name} className="grid-row auto" style={{ gridTemplateColumns: APP_COLUMNS, gap: 14, alignItems: 'start' }} data-testid={`app-row-${a.name}`} data-selected={p.selected === a.name ? 'true' : undefined}>
            <span><span className="mono" data-testid="app-name">{a.name}</span>{a.title && <div className="hint">{a.title}{a.kind === 'portal' ? ' (portal)' : ''}</div>}</span>
            <span data-testid="app-serves">version {a.version}{a.manifest_version ? ` (${a.manifest_version})` : ''}<div className="hint">by {a.published_by}</div></span>
            <span>{a.trust ?? '—'}</span>
            <span>{a.versions.length}</span>
            <span className="mono" data-testid="app-instances">{a.instances.length === 0 ? '—' : a.instances.join(', ')}</span>
            <span>
              <span className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
                <button type="button" className="btn sm" onClick={() => p.onSelect(p.selected === a.name ? null : a.name)} data-testid={`app-open-${a.name}`}>{p.selected === a.name ? 'Hide versions' : 'Versions and data access'}</button>
                {p.confirming === a.name
                  ? (
                    <>
                      <button type="button" className="btn sm btn-primary" disabled={p.busy} onClick={() => p.onRemove(a.name)} data-testid={`app-remove-confirm-${a.name}`}>Stop serving {a.name}</button>
                      <button type="button" className="btn sm" onClick={() => p.onAskRemove(null)}>Not now</button>
                    </>
                  )
                  : <button type="button" className="btn sm" disabled={reason !== null || p.busy} title={reason ?? undefined} onClick={() => p.onAskRemove(a.name)} data-testid={`app-remove-${a.name}`}>Remove</button>}
              </span>
              <Why reason={reason} testId={`app-remove-why-${a.name}`} />
            </span>
          </div>
        )
      })}
    </div>
  )
}

/** What an app's version declares it can reach, who published it and who approved it: the manifest's own words (AP18). */
export function AboutView({ about, version }: { about: AppAbout; version: number }) {
  return (
    <div className="card-body" data-testid={`about-${version}`}>
      <table className="kv"><tbody>
        <tr><th style={{ textAlign: 'left', paddingRight: 24 }}>Published by</th><td data-testid="about-published-by">{about.published_by} on {when(about.published_at)}</td></tr>
        <tr><th style={{ textAlign: 'left', paddingRight: 24 }}>Runs as</th><td data-testid="about-trust">{about.trust}</td></tr>
        {about.trust === 'trusted' && (
          <tr><th style={{ textAlign: 'left', paddingRight: 24 }}>Approval</th><td data-testid="about-approval">{about.approval ? `${about.approval.approved_by} on ${when(about.approval.approved_at)} (${about.approval.id})${about.approval.note ? `: ${about.approval.note}` : ''}` : 'none for these files: the app does not run as trusted'}</td></tr>
        )}
        {about.roles.length > 0 && <tr><th style={{ textAlign: 'left', paddingRight: 24 }}>Roles it knows</th><td className="mono">{about.roles.join(', ')}</td></tr>}
        {about.storage && <tr><th style={{ textAlign: 'left', paddingRight: 24 }}>Storage</th><td data-testid="about-storage">{about.storage.user && about.storage.instance ? 'per person and shared' : about.storage.user ? 'per person' : 'shared'}, up to {about.storage.quota_mb} MB</td></tr>}
      </tbody></table>
      <h3 className="hint" style={{ margin: '12px 0 4px' }}>Data it can reach</h3>
      {about.models.length === 0 && <div data-testid="about-models-none">None.</div>}
      <ul data-testid="about-models" style={{ margin: 0, paddingLeft: 18 }}>
        {about.models.map((m) => <li key={`${m.model}.${m.entity}`} className="mono" data-testid="about-model">{m.model}.{m.entity}: {m.operations}</li>)}
        {about.semantic_models.map((m) => <li key={`s-${m}`} className="mono">semantic model {m}: read</li>)}
      </ul>
      <h3 className="hint" style={{ margin: '12px 0 4px' }}>Outside addresses it may use</h3>
      {about.egress.length === 0 && <div data-testid="about-egress-none">None.</div>}
      <ul data-testid="about-egress" style={{ margin: 0, paddingLeft: 18 }}>
        {about.egress.map((e) => <li key={e.directive} className="mono">{e.directive}: {e.origins.join(', ')}</li>)}
      </ul>
      <p className="hint">What it may do is also bounded by each person's own roles.</p>
    </div>
  )
}

export interface VersionsViewProps {
  detail: AppDetail
  by: string
  note: string
  /** The version whose approval form is open. */
  approving: number | null
  /** The version whose data access is shown. */
  showing: number | null
  busy: boolean
  onServe: (version: number) => void
  onShow: (version: number | null) => void
  onAskApprove: (version: number | null) => void
  onNote: (text: string) => void
  onApprove: (version: number) => void
}

const VERSION_COLUMNS = '0.6fr 1fr 1.6fr 1.2fr 1.4fr 2.4fr'

/** An app's versions, newest first: who published each and when, whether it serves, the approval standing for its exact files, and the actions: serve this one (roll back), approve, and see what it can reach. */
export function VersionsView(p: VersionsViewProps) {
  const { detail } = p
  return (
    <div className="card flush" data-testid={`versions-${detail.name}`}>
      <div className="card-head"><span className="card-title">Versions of {detail.name}</span></div>
      <div className="grid-head" style={{ gridTemplateColumns: VERSION_COLUMNS, gap: 14 }}><span>Version</span><span>Manifest</span><span>Published</span><span>Trust</span><span>Approval</span><span>Actions</span></div>
      {detail.versions.map((v: AppVersionRow) => {
        const serveReason = serveBlock(detail.caller, v)
        const approveReason = approveBlock(detail.caller, v, p.by)
        return (
          <div key={v.version}>
            <div className="grid-row auto" style={{ gridTemplateColumns: VERSION_COLUMNS, gap: 14, alignItems: 'start' }} data-testid={`version-row-${v.version}`} data-serving={v.serving ? 'true' : undefined}>
              <span className="mono">{v.version}{v.serving ? ' (serving)' : ''}</span>
              <span className="mono">{v.manifest_version ?? '—'}</span>
              <span>{v.published_by}<div className="hint">{when(v.published_at)}{v.note ? `: ${v.note}` : ''}</div></span>
              <span>{v.trust ?? '—'}</span>
              <span data-testid="version-approval">{v.trust !== 'trusted' ? '—' : v.approval_id ?? 'none'}<div className="hint mono">{shortHash(v.hash)}</div></span>
              <span>
                <span className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
                  <button type="button" className="btn sm" disabled={serveReason !== null || p.busy} title={serveReason ?? undefined} onClick={() => p.onServe(v.version)} data-testid={`version-serve-${v.version}`}>Serve this version</button>
                  {v.trust === 'trusted' && !v.approval_id && <button type="button" className="btn sm" disabled={!detail.caller.can_approve} title={!detail.caller.can_approve ? approveReason ?? undefined : undefined} onClick={() => p.onAskApprove(p.approving === v.version ? null : v.version)} data-testid={`version-approve-${v.version}`}>Approve…</button>}
                  <button type="button" className="btn sm" onClick={() => p.onShow(p.showing === v.version ? null : v.version)} data-testid={`version-about-${v.version}`}>Data access</button>
                </span>
                {!detail.caller.can_publish && <Why reason={serveReason} testId={`version-serve-why-${v.version}`} />}
                {v.trust === 'trusted' && !v.approval_id && !detail.caller.can_approve && <Why reason={approveReason} testId={`version-approve-why-${v.version}`} />}
              </span>
            </div>
            {p.approving === v.version && (
              <div className="card-body" data-testid={`approve-form-${v.version}`} style={{ borderTop: '1px solid var(--line, #ddd)' }}>
                <p style={{ margin: '0 0 8px' }}>Approving records that <b>{detail.name} {v.manifest_version}</b>, these exact files (hash <span className="mono">{shortHash(v.hash)}</span>), may run as trusted. Any changed byte needs a new approval.</p>
                <p className="hint" style={{ margin: '0 0 8px' }}>The approver is named in the audit: {p.by || 'name the approver above'}. The Proxy checks that they hold the Approver role and are not {v.published_by}, who published this version.</p>
                <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
                  <input className="input" placeholder="note (a link to the review, for example)" value={p.note} onChange={(e) => p.onNote(e.target.value)} data-testid="approve-note" aria-label="Note" />
                  <button type="button" className="btn btn-primary" disabled={approveReason !== null || p.busy} onClick={() => p.onApprove(v.version)} data-testid={`approve-confirm-${v.version}`}>Approve these files</button>
                </div>
                <Why reason={approveReason} testId="approve-why" />
              </div>
            )}
            {p.showing === v.version && v.about && <AboutView about={v.about} version={v.version} />}
          </div>
        )
      })}
    </div>
  )
}

// ---- navigation sets ----

/** The navigation sets: the menus the shell shows in each member instance, with their members in order. */
export function NavigationView({ sets }: { sets: NavigationSetRow[] }) {
  return (
    <div className="card flush" data-testid="navigation-table">
      {sets.length === 0 && <div className="empty" data-testid="navigation-empty">No navigation set yet. One is made with <span className="mono">apps nav-save</span>.</div>}
      {sets.map((s) => (
        <div key={s.name} className="grid-row auto" style={{ gridTemplateColumns: '1.3fr 1.5fr 4fr', gap: 14 }} data-testid={`nav-row-${s.name}`}>
          <span className="mono">{s.name}</span><span>{s.title}</span>
          <span className="mono" data-testid="nav-entries">{s.entries.map((e) => e.label ? `${e.instance} (${e.label})` : e.instance).join(' → ')}</span>
        </div>
      ))}
    </div>
  )
}

// ---- approvals ----

export interface ApprovalsViewProps {
  rows: AppApprovalRow[]
  caller: AppsCaller
  by: string
  confirming: string | null
  busy: boolean
  onAskRevoke: (id: string | null) => void
  onRevoke: (id: string) => void
}

const APPROVAL_COLUMNS = '1.2fr 1fr 1.6fr 1.5fr 1.4fr 2fr'

/** Who approved which content hash, for what, when, and whether it still stands; revoking needs the Approver role like approving does. */
export function ApprovalsView(p: ApprovalsViewProps) {
  return (
    <div className="card flush" data-testid="approvals-table">
      <div className="grid-head" style={{ gridTemplateColumns: APPROVAL_COLUMNS, gap: 14 }}><span>App</span><span>Version</span><span>Approved by</span><span>Content hash</span><span>Status</span><span>Actions</span></div>
      {p.rows.length === 0 && <div className="empty" data-testid="approvals-empty">No approval yet. A trusted app is approved before it can be published or run.</div>}
      {p.rows.map((a) => {
        const reason = revokeBlock(p.caller, a, p.by)
        return (
          <div key={a.id} className="grid-row auto" style={{ gridTemplateColumns: APPROVAL_COLUMNS, gap: 14, alignItems: 'start' }} data-testid={`approval-row-${a.id}`} data-revoked={a.revoked ? 'true' : 'false'}>
            <span><span className="mono">{a.app}</span><div className="hint mono">{a.id}</div></span>
            <span>{a.version}{a.published_version != null ? <div className="hint">published as {a.published_version}</div> : null}</span>
            <span>{a.approved_by}<div className="hint">{when(a.approved_at)}{a.note ? `: ${a.note}` : ''}</div></span>
            <span><span className="mono" title={a.hash}>{shortHash(a.hash)}</span>
              {(a.operations.length > 0 || a.egress.length > 0) && (
                <div className="hint" data-testid="approval-for">for {[...a.operations.map((o) => `${o.model}.${o.entity} ${o.operations}`), ...a.egress.map((e) => `${e.directive} ${e.origins.join(' ')}`)].join('; ')}</div>
              )}
            </span>
            <span data-testid="approval-status">{a.revoked ? `revoked ${a.revoked_at ? when(a.revoked_at) : ''}` : 'standing'}</span>
            <span>
              {a.revoked ? <span className="hint">—</span> : p.confirming === a.id
                ? (
                  <span className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
                    <button type="button" className="btn sm btn-primary" disabled={reason !== null || p.busy} onClick={() => p.onRevoke(a.id)} data-testid={`approval-revoke-confirm-${a.id}`}>Revoke as {p.by || '…'}</button>
                    <button type="button" className="btn sm" onClick={() => p.onAskRevoke(null)}>Not now</button>
                  </span>
                )
                : <button type="button" className="btn sm" disabled={!p.caller.can_approve || p.busy} title={reason ?? undefined} onClick={() => p.onAskRevoke(a.id)} data-testid={`approval-revoke-${a.id}`}>Revoke…</button>}
              {!a.revoked && !p.caller.can_approve && <Why reason={reason} testId={`approval-revoke-why-${a.id}`} />}
              {!a.revoked && p.confirming === a.id && <Why reason={reason} testId="revoke-why" />}
            </span>
          </div>
        )
      })}
    </div>
  )
}

// ---- the screen ----

/**
 * Apps (plan 26, AP24): what is published on this Proxy and how it is run: the apps with their versions, the instances with the version each runs (pinned or following) and its address, the navigation sets and portals, and the
 * approvals of trusted apps. A person with the role may roll an app back, pin an instance, approve exact files, revoke an approval and stop serving an instance or app; the Proxy checks the role on every call, and the screen says
 * why a button is off. Reads are for a viewer, so there is no administrator gate here.
 */
export function AppsPage() {
  const queryClient = useQueryClient()
  const apps = useQuery({ queryKey: ['proxy', 'apps'], queryFn: proxyApi.apps.list, refetchInterval: 10_000 })
  const instances = useQuery({ queryKey: ['proxy', 'app-instances'], queryFn: proxyApi.apps.instances, refetchInterval: 10_000 })
  const navigation = useQuery({ queryKey: ['proxy', 'app-navigation'], queryFn: proxyApi.apps.navigation })
  const approvals = useQuery({ queryKey: ['proxy', 'app-approvals'], queryFn: proxyApi.apps.approvals, refetchInterval: 10_000 })
  const [selected, setSelected] = useState<string | null>(null)
  const detail = useQuery({ queryKey: ['proxy', 'apps', selected], queryFn: () => proxyApi.apps.detail(selected!), enabled: selected !== null })
  const [chosen, setChosen] = useState<Record<string, string>>({})
  const [confirming, setConfirming] = useState<string | null>(null)
  const [approving, setApproving] = useState<number | null>(null)
  const [showing, setShowing] = useState<number | null>(null)
  const [note, setNote] = useState('')
  const [by, setBy] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const caller: AppsCaller = apps.data?.caller ?? instances.data?.caller ?? approvals.data?.caller ?? { can_publish: false, can_approve: false, you: '' }
  const approver = by ?? caller.you
  // a failure is shown once, at the top: the Proxy's own words (a store's refusal names what to do first)
  const [failure, setFailure] = useState<unknown>(null)
  const done = (text: string) => async () => { setFailure(null); setNotice(text); setConfirming(null); await queryClient.invalidateQueries({ queryKey: ['proxy'] }) }
  const failed = (error: unknown) => { setNotice(null); setConfirming(null); setFailure(error) }
  useEffect(() => { if (selected !== null && apps.data && !apps.data.apps.some((a) => a.name === selected)) setSelected(null) }, [apps.data, selected])

  const serve = useMutation({ mutationFn: (v: { app: string; to: number }) => proxyApi.apps.rollback(v.app, v.to), onSuccess: (r) => done(`${r.name} now serves version ${r.version}.`)(), onError: failed })
  const pin = useMutation({
    mutationFn: (v: { row: AppInstanceRow; to: number | null }) => proxyApi.apps.savePin(v.row, v.to),
    onSuccess: async (r, v) => { setChosen((c) => { const next = { ...c }; delete next[v.row.name]; return next }); await done(v.to === null ? `${r.name} follows ${r.app} again.` : `${r.name} is pinned to version ${v.to}.`)() },
    onError: failed,
  })
  const removeInstance = useMutation({ mutationFn: proxyApi.apps.removeInstance, onSuccess: (r) => done(`${r.name} is no longer served.`)(), onError: failed })
  const removeApp = useMutation({ mutationFn: proxyApi.apps.removeApp, onSuccess: (r) => done(`${r.name} is no longer served; its versions and approvals stay on record.`)(), onError: failed })
  const approve = useMutation({
    mutationFn: (v: { app: string; version: number }) => proxyApi.apps.approve(v.app, v.version, approver, note),
    onSuccess: async (r) => { setApproving(null); setNote(''); await done(`Approved (${r.id}).`)() },
    onError: failed,
  })
  const revoke = useMutation({ mutationFn: (id: string) => proxyApi.apps.revoke(id, approver), onSuccess: (r) => done(`Revoked ${r.id}; a trusted version it stood for stops running as trusted at once.`)(), onError: failed })
  const busy = serve.isPending || pin.isPending || removeInstance.isPending || removeApp.isPending || approve.isPending || revoke.isPending

  const appList = sortApps(apps.data?.apps ?? [])
  const rows = sortInstances(instances.data?.instances ?? [])
  const loading = apps.isLoading || instances.isLoading
  return (
    <ProxyShell crumbs={[{ label: 'Apps', heading: true }]}>
      <div className="pane" data-testid="proxy-apps">
        <div className="page-head">
          <h1 className="page-title">Apps</h1>
          <span className="page-note" data-testid="apps-caller">{caller.can_publish ? 'You may roll back, pin and remove.' : 'You may look; changing apps needs the publisher or administrator role.'} {caller.can_approve ? 'You may approve and revoke.' : 'Approving needs the Approver role.'}</span>
        </div>
        <ErrorBanner error={apps.error ?? instances.error ?? approvals.error ?? navigation.error ?? failure} />
        {notice && <div className="card" role="status" data-testid="apps-notice"><div className="card-body">{notice}</div></div>}
        {loading && <div className="empty" data-testid="apps-loading">Loading…</div>}

        <Section title="Instances" note="An instance is an app served at its own address. Pinned, it keeps its version whatever the app serves; following, it moves with the app." testId="apps-instances">
          <InstancesView
            rows={rows} versionsOf={(app) => appList.find((a) => a.name === app)?.versions ?? []} caller={caller} appsAddress={instances.data?.apps_address ?? null}
            chosen={chosen} confirming={confirming?.startsWith('instance:') ? confirming.slice(9) : null} busy={busy}
            onChoose={(name, value) => setChosen((c) => ({ ...c, [name]: value }))}
            onPin={(row) => pin.mutate({ row, to: (chosen[row.name] ?? '') === '' ? null : Number(chosen[row.name]) })}
            onAskRemove={(name) => setConfirming(name === null ? null : `instance:${name}`)} onRemove={(name) => removeInstance.mutate(name)}
          />
        </Section>

        <Section title="Apps" note="What is published, and the version each serves. Roll an app back by serving an older version; instances that follow it move at once." testId="apps-apps">
          <AppsView
            apps={appList} selected={selected} caller={caller} confirming={confirming?.startsWith('app:') ? confirming.slice(4) : null} busy={busy}
            onSelect={(name) => { setSelected(name); setApproving(null); setShowing(null) }}
            onAskRemove={(name) => setConfirming(name === null ? null : `app:${name}`)} onRemove={(name) => removeApp.mutate(name)}
          />
          {selected !== null && detail.isLoading && <div className="empty" data-testid="app-detail-loading">Loading…</div>}
          {selected !== null && detail.data && (
            <div style={{ marginTop: 12 }}>
              <VersionsView
                detail={detail.data} by={approver} note={note} approving={approving} showing={showing} busy={busy}
                onServe={(version) => serve.mutate({ app: selected, to: version })} onShow={setShowing}
                onAskApprove={(version) => { setApproving(version); setFailure(null) }} onNote={setNote} onApprove={(version) => approve.mutate({ app: selected, version })}
              />
            </div>
          )}
        </Section>

        <Section title="Navigation sets and portals" note="A navigation set is a menu the shell shows in each of its instances. A portal is an app of kind portal: its mounts are shown on its instance above." testId="apps-navigation">
          <NavigationView sets={navigation.data ?? []} />
        </Section>

        <Section title="Approvals" note="An approval is for the exact files of one version, by content hash. Revoking it stops that version running as trusted at once." testId="apps-approvals">
          <div className="row" style={{ gap: 8, marginBottom: 8, alignItems: 'center' }}>
            <label className="hint" htmlFor="approver-name">Approver (a person of the identity home who holds the Approver role on this server)</label>
            <input id="approver-name" className="input sm" style={{ width: 220, flex: 'none' }} value={approver} onChange={(e) => setBy(e.target.value)} data-testid="approver-name" />
          </div>
          <ApprovalsView
            rows={sortApprovals(approvals.data?.approvals ?? [])} caller={caller} by={approver} confirming={confirming?.startsWith('revoke:') ? confirming.slice(7) : null} busy={busy}
            onAskRevoke={(id) => setConfirming(id === null ? null : `revoke:${id}`)} onRevoke={(id) => revoke.mutate(id)}
          />
        </Section>
        <p className="hint" style={{ marginTop: 16 }}>Publishing a new version, making an instance and the navigation sets are still done with the command line (<span className="mono">dbdataproxy apps</span>, also <span className="mono">--to</span> this server from another machine).</p>
      </div>
    </ProxyShell>
  )
}
