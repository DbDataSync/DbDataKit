import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { ConsoleShell } from '../ConsoleShell'
import { BindingsPanel } from './BindingsPage'
import {
  alertFormFrom, alertSummary, alertsApi, buildAlertRule, buildRequest, channelProblem, deliveryApi, describeSchedule, emptyForm, formFromSubscription, outboxLine, sendNowBlockedReason, statusWord, subscriptionProblem,
  subscriptionStatusWord, type AlertForm, type AlertRule, type DeliveryMessage, type DeliveryOverview, type Subscription, type SubscriptionForm,
} from '../delivery'

type Tab = 'channels' | 'subscriptions' | 'alerts' | 'history'
const TABS: { id: Tab; label: string }[] = [{ id: 'channels', label: 'Channels' }, { id: 'subscriptions', label: 'Subscriptions' }, { id: 'alerts', label: 'Alerts' }, { id: 'history', label: 'History' }]

/**
 * Delivery (plan 22a GP18): the channels of the install's `delivery:` section with the connection each uses per release channel, whether each can be used and a test send, a person's subscriptions to a dashboard or report (and, for an administrator, everyone's), and the outbox with what the far side
 * said on each try. The server decides everything shown here; a screen never works out what is allowed. A secret is never shown, only the reference.
 */
export function DeliveryPage() {
  const [params, setParams] = useSearchParams()
  const tab = (TABS.find((t) => t.id === params.get('tab'))?.id ?? 'channels') as Tab
  const overview = useQuery({ queryKey: ['delivery'], queryFn: deliveryApi.overview })
  const failed = overview.data?.outbox.dead ?? 0
  return (
    <ConsoleShell crumbs={[{ label: 'Delivery', heading: true }]}>
      <div className="pane" data-testid="delivery-page">
        <div className="page-head">
          <h1 className="page-title">Delivery</h1>
          <span className="page-note">How reports, alerts and subscriptions leave the suite: the channels, who is subscribed to what, and what happened to each message.</span>
        </div>
        <ErrorBanner error={overview.error} />
        {overview.data?.problem && <div className="error" role="alert" data-testid="delivery-problem">The delivery settings cannot be used: {overview.data.problem}</div>}
        <div className="subtabbar" data-testid="delivery-tabs">
          {TABS.map((t) => (
            <button key={t.id} type="button" className={`subtab ${tab === t.id ? 'active' : ''}`} onClick={() => setParams({ tab: t.id })} data-testid={`delivery-tab-${t.id}`}>
              {t.label}{t.id === 'history' && failed > 0 ? ` (${failed} failed)` : ''}
            </button>
          ))}
        </div>
        {overview.data && tab === 'channels' && <ChannelsTab overview={overview.data} />}
        {overview.data && tab === 'subscriptions' && <SubscriptionsTab overview={overview.data} />}
        {overview.data && tab === 'alerts' && <AlertsTab />}
        {overview.data && tab === 'history' && <HistoryTab overview={overview.data} />}
      </div>
    </ConsoleShell>
  )
}

// ---- channels ----

/**
 * The channels (plan 22a GP18, folded into connections, then into bindings by plan 27a IX23): which connection each delivery channel name uses in each release channel is a binding, shown and edited by the one Bindings panel (with the Test send through a
 * release channel's connection); this tab adds what only delivery knows, the outbox of each channel and the retry policy. A release channel with no entry has no channel, so a test cannot notify production's.
 */
function ChannelsTab({ overview }: { overview: DeliveryOverview }) {
  return (
    <div data-testid="delivery-channels">
      <BindingsPanel kinds={['delivery']} showRunner={false} showFile={false} />
      {overview.channels.map((c) => (
        <div key={c.name} className="hint" data-testid={`delivery-outbox-${c.name}`} data-ready={c.ready}>
          <strong>{c.name}</strong> ({c.kind}{c.limitPerMinute ? `, at most ${c.limitPerMinute} a minute` : ''}): {c.ready ? 'ready' : channelProblem(c) ?? 'cannot be used'}. Outbox: {outboxLine(c.outbox)}
        </div>
      ))}
      {overview.channels.length > 0 && <div className="hint" style={{ marginTop: 8 }}>Messages are tried up to {overview.policy.maxAttempts} times, waiting {overview.policy.backoffSeconds.join(', ')} seconds between tries; then they are parked as failed (History) until an administrator sends them again.</div>}
    </div>
  )
}

// ---- alerts (plan 22a GP16) ----

/**
 * The alerts (plan 22a GP16): the fixed set of kinds, each on or off with the channel and recipients it goes to (no channel: the notification feed only) and the cool-down per source, and what is failing now and has been told. An administrator's
 * screen; the server says what a rule may name.
 */
function AlertsTab() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const overview = useQuery({ queryKey: ['delivery', 'alerts'], queryFn: alertsApi.overview, enabled: isAdmin })
  const [editing, setEditing] = useState<{ rule: AlertRule; form: AlertForm } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: async ({ rule, form }: { rule: AlertRule; form: AlertForm }) => {
      const built = buildAlertRule(form, rule.version)
      if ('error' in built) throw new Error(built.error)
      return alertsApi.set(rule.kind, built.body)
    },
    onSuccess: async () => { setEditing(null); setError(null); await client.invalidateQueries({ queryKey: ['delivery', 'alerts'] }) },
    onError: (e) => setError(e instanceof Error ? e.message : String(e)),
  })
  if (!isAdmin) return <div className="empty" data-testid="alerts-admin-only">Alerts are set by an administrator.</div>
  const data = overview.data
  return (
    <div data-testid="delivery-alerts">
      <div className="hint">Alerts are on only where an administrator turns them on. A source that keeps failing is told once, again after the cool-down, and once more when it recovers; with no channel an alert is shown in the notification feed only.</div>
      <ErrorBanner error={overview.error} />
      {data && (
        <table className="table" data-testid="alerts-table">
          <thead><tr><th>Alert</th><th>Rule</th><th /></tr></thead>
          <tbody>
            {data.rules.map((r) => (
              <tr key={r.kind} data-testid={`alert-${r.kind}`} data-enabled={r.enabled}>
                <td><strong>{r.title}</strong></td>
                <td>{alertSummary(r)}</td>
                <td><button type="button" className="btn btn-sm" onClick={() => { setError(null); setEditing({ rule: r, form: alertFormFrom(r) }) }} data-testid={`alert-edit-${r.kind}`}>Edit</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {editing && data && (
        <div className="card" data-testid="alert-editor">
          <h3>{editing.rule.title}</h3>
          <label><input type="checkbox" checked={editing.form.enabled} onChange={(e) => setEditing({ ...editing, form: { ...editing.form, enabled: e.target.checked } })} data-testid="alert-enabled" /> On</label>
          <label>Channel
            <select value={editing.form.channel} onChange={(e) => setEditing({ ...editing, form: { ...editing.form, channel: e.target.value } })} data-testid="alert-channel">
              <option value="">none: the notification feed only</option>
              {data.channels.map((c) => <option key={c} value={c}>{c}</option>)}
            </select>
          </label>
          <label>Recipients (email addresses; a webhook channel needs none)
            <input value={editing.form.recipients} onChange={(e) => setEditing({ ...editing, form: { ...editing.form, recipients: e.target.value } })} data-testid="alert-recipients" />
          </label>
          <label>Cool-down in minutes
            <input value={editing.form.cooldownMinutes} onChange={(e) => setEditing({ ...editing, form: { ...editing.form, cooldownMinutes: e.target.value } })} data-testid="alert-cooldown" />
          </label>
          <label>Release channel (empty: prod)
            <select value={editing.form.releaseChannel} onChange={(e) => setEditing({ ...editing, form: { ...editing.form, releaseChannel: e.target.value } })} data-testid="alert-release">
              <option value="">prod</option>
              {data.releaseChannels.filter((c) => c !== 'prod').map((c) => <option key={c} value={c}>{c}</option>)}
            </select>
          </label>
          {error && <div className="error" role="alert" data-testid="alert-error">{error}</div>}
          <div className="row" style={{ gap: 8 }}>
            <button type="button" className="btn btn-sm btn-primary" disabled={save.isPending} onClick={() => save.mutate(editing)} data-testid="alert-save">Save</button>
            <button type="button" className="btn btn-sm" onClick={() => setEditing(null)}>Cancel</button>
          </div>
        </div>
      )}
      {data && (
        <div style={{ marginTop: 12 }} data-testid="alerts-failing">
          <h3>Failing now</h3>
          {data.failing.length === 0
            ? <div className="empty" data-testid="alerts-none">Nothing that has been told is still failing.</div>
            : <ul>{data.failing.map((f) => <li key={`${f.source}|${f.recipient ?? ''}`}>{f.message ?? f.source} <span className="muted">(told {new Date(f.lastSentAt).toLocaleString()}{f.recipient ? ` to ${f.recipient}` : ''})</span></li>)}</ul>}
        </div>
      )}
    </div>
  )
}

// ---- subscriptions ----

function SubscriptionsTab({ overview }: { overview: DeliveryOverview }) {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const [all, setAll] = useState(false)
  const [editing, setEditing] = useState<{ id: string | null; version: number; form: SubscriptionForm } | null>(null)
  const [outcome, setOutcome] = useState<{ ok: boolean; text: string } | null>(null)
  const [open, setOpen] = useState<string | null>(null)
  const list = useQuery({ queryKey: ['delivery', 'subscriptions', all], queryFn: () => deliveryApi.subscriptions(all) })
  const refresh = () => client.invalidateQueries({ queryKey: ['delivery'] })
  const act = async (label: string, fn: () => Promise<unknown>) => {
    setOutcome(null)
    try { await fn(); await refresh() } catch (e) { setOutcome({ ok: false, text: `${label}: ${e instanceof Error ? e.message : String(e)}` }) }
  }
  const rows = list.data?.subscriptions ?? []
  return (
    <div data-testid="delivery-subscriptions">
      <div className="row" style={{ gap: 12, alignItems: 'center', marginBottom: 8 }}>
        <button type="button" className="btn btn-sm btn-primary" onClick={() => setEditing({ id: null, version: 0, form: emptyForm(overview.channels[0]?.name ?? '') })} data-testid="subscription-new">Subscribe to a dashboard or report</button>
        {isAdmin && <label><input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} data-testid="subscriptions-all" /> everyone&apos;s</label>}
      </div>
      {!overview.canRender && <div className="hint" data-testid="subscriptions-render-note">{overview.renderNote}</div>}
      <ErrorBanner error={list.error} />
      {outcome && <div className={outcome.ok ? 'hint' : 'error'} role={outcome.ok ? 'status' : 'alert'} data-testid="subscription-outcome">{outcome.text}</div>}
      {editing && <SubscriptionEditor channels={overview.channels.map((c) => c.name)} editing={editing} onCancel={() => setEditing(null)}
        onSave={async (body) => {
          try {
            if (editing.id) await deliveryApi.update(editing.id, editing.version, body)
            else await deliveryApi.subscribe(body)
            setEditing(null)
            await refresh()
            return null
          } catch (e) { return e instanceof Error ? e.message : String(e) }
        }} />}
      {list.data && rows.length === 0 && <div className="empty" data-testid="subscriptions-empty">{all ? 'Nobody is subscribed to anything yet.' : 'You are not subscribed to anything. Subscribe to a dashboard or a report and it comes to you on the schedule you choose, drawn as you are allowed to see it.'}</div>}
      {rows.length > 0 && (
        <table className="table" data-testid="subscriptions-table">
          <thead><tr><th>Name</th>{all && <th>Person</th>}<th>Document</th><th>Schedule</th><th>Channel</th><th>State</th><th /></tr></thead>
          <tbody>
            {rows.map((s) => (
              <SubscriptionRow key={s.id} s={s} all={all} open={open === s.id} canRender={overview.canRender} renderNote={overview.renderNote}
                onToggle={() => setOpen(open === s.id ? null : s.id)}
                onEdit={() => setEditing({ id: s.id, version: s.version, form: formFromSubscription(s) })}
                onPause={() => void act('Pause', () => deliveryApi.pause(s.id))} onResume={() => void act('Resume', () => deliveryApi.resume(s.id))}
                onRemove={() => void act('Remove', () => deliveryApi.remove(s.id))}
                onRun={() => void act('Send now', async () => { const a = await deliveryApi.run(s.id); setOutcome({ ok: a.result.outcome === 'sent', text: `${s.name}: ${a.result.outcome}. ${a.result.detail}` }) })} />
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

function SubscriptionRow(props: { s: Subscription; all: boolean; open: boolean; canRender: boolean; renderNote: string | null; onToggle: () => void; onEdit: () => void; onPause: () => void; onResume: () => void; onRemove: () => void; onRun: () => void }) {
  const { s, all, open, canRender, renderNote } = props
  const problem = subscriptionProblem(s)
  const blocked = sendNowBlockedReason(canRender, renderNote, s)
  const runs = useQuery({ queryKey: ['delivery', 'runs', s.id, s.lastRunAt], queryFn: () => deliveryApi.runs(s.id), enabled: open })
  return (
    <>
      <tr data-testid={`subscription-${s.name}`} data-status={s.status}>
        <td><button type="button" className="btn btn-sm" onClick={props.onToggle} aria-expanded={open} data-testid={`subscription-runs-${s.name}`}>{s.name}</button></td>
        {all && <td>{s.personName}</td>}
        <td>{s.kind} <span className="mono">{s.view}</span> · {s.format}{s.widget ? ` of ${s.widget}` : ''}{s.layout ? ` · layout ${s.layout}` : ''}</td>
        <td>{describeSchedule(s.schedule.cron, s.schedule.timeZone)}{s.nextRunAt ? <div className="hint">next {s.nextRunAt}</div> : null}</td>
        <td>{s.channel}</td>
        <td data-testid={`subscription-state-${s.name}`}>{subscriptionStatusWord(s)}{problem && <div className="error" data-testid={`subscription-problem-${s.name}`}>{problem}</div>}</td>
        <td>
          <button type="button" className="btn btn-sm" onClick={props.onEdit} data-testid={`subscription-edit-${s.name}`}>Edit</button>{' '}
          {s.status === 'active'
            ? <button type="button" className="btn btn-sm" onClick={props.onPause} data-testid={`subscription-pause-${s.name}`}>Pause</button>
            : <button type="button" className="btn btn-sm" onClick={props.onResume} data-testid={`subscription-resume-${s.name}`}>Resume</button>}{' '}
          <button type="button" className="btn btn-sm" disabled={blocked !== null} title={blocked ?? undefined} onClick={props.onRun} data-testid={`subscription-run-${s.name}`}>Send now</button>{' '}
          <button type="button" className="btn btn-sm" onClick={props.onRemove} data-testid={`subscription-remove-${s.name}`}>Remove</button>
        </td>
      </tr>
      {open && (
        <tr data-testid={`subscription-detail-${s.name}`}>
          <td colSpan={all ? 7 : 6}>
            <ErrorBanner error={runs.error} />
            {runs.data && runs.data.runs.length === 0 && <div className="hint">No run yet.</div>}
            {runs.data && runs.data.runs.length > 0 && (
              <table className="table"><thead><tr><th>When</th><th>Result</th><th>What happened</th></tr></thead>
                <tbody>{runs.data.runs.map((r, i) => <tr key={i} data-testid="subscription-run" data-outcome={r.outcome}><td>{r.startedAt}</td><td>{r.outcome}</td><td>{r.detail}</td></tr>)}</tbody></table>
            )}
          </td>
        </tr>
      )}
    </>
  )
}

function SubscriptionEditor(props: { channels: string[]; editing: { id: string | null; version: number; form: SubscriptionForm }; onCancel: () => void; onSave: (body: Record<string, unknown>) => Promise<string | null> }) {
  const [form, setForm] = useState<SubscriptionForm>(props.editing.form)
  const [error, setError] = useState<string | null>(null)
  const set = <K extends keyof SubscriptionForm>(key: K, value: SubscriptionForm[K]) => setForm({ ...form, [key]: value })
  const save = async () => {
    const built = buildRequest(form)
    if ('error' in built) { setError(built.error); return }
    setError(await props.onSave(built.body))
  }
  return (
    <div className="card" style={{ marginBottom: 12 }} data-testid="subscription-editor">
      <div className="card-body">
        <strong>{props.editing.id ? 'Edit the subscription' : 'Subscribe'}</strong>
        <div className="grid" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: 8, margin: '8px 0' }}>
          <label>Name<input className="input" value={form.name} onChange={(e) => set('name', e.target.value)} data-testid="subscription-name" /></label>
          <label>Kind<select className="input" value={form.kind} onChange={(e) => set('kind', e.target.value as SubscriptionForm['kind'])} data-testid="subscription-kind"><option value="dashboard">dashboard</option><option value="report">report</option></select></label>
          <label>Which one<input className="input" value={form.view} onChange={(e) => set('view', e.target.value)} placeholder="its name" data-testid="subscription-view" /></label>
          <label>Format<select className="input" value={form.format} onChange={(e) => set('format', e.target.value as SubscriptionForm['format'])} data-testid="subscription-format"><option value="pdf">PDF</option><option value="csv">CSV (one widget)</option><option value="xlsx">Excel workbook</option></select></label>
          {form.format !== 'pdf' && <label>Widget{form.format === 'csv' ? '' : ' (all tables when empty)'}<input className="input" value={form.widget} onChange={(e) => set('widget', e.target.value)} data-testid="subscription-widget" /></label>}
          {form.format === 'pdf' && <label>Layout (the document&apos;s print rule when empty)<input className="input" value={form.layout} onChange={(e) => set('layout', e.target.value)} data-testid="subscription-layout" /></label>}
          <label>Channel<select className="input" value={form.channel} onChange={(e) => set('channel', e.target.value)} data-testid="subscription-channel">{props.channels.map((c) => <option key={c} value={c}>{c}</option>)}</select></label>
          <label>Send to (your own address when empty)<input className="input" value={form.recipient} onChange={(e) => set('recipient', e.target.value)} data-testid="subscription-recipient" /></label>
          <label>When (cron: minute hour day month weekday)<input className="input mono" value={form.cron} onChange={(e) => set('cron', e.target.value)} data-testid="subscription-cron" /></label>
          <label>Time zone<input className="input" value={form.timeZone} onChange={(e) => set('timeZone', e.target.value)} data-testid="subscription-zone" /></label>
        </div>
        <label>The document&apos;s parameters, as JSON (optional)<textarea className="input mono" rows={2} style={{ width: '100%' }} value={form.parameters} onChange={(e) => set('parameters', e.target.value)} data-testid="subscription-parameters" /></label>
        <div className="hint">A subscription is at most hourly. It is drawn as you: you receive the rows you are allowed to see, and if you lose access it stops and tells you and the person who set it up.</div>
        {error && <div className="error" role="alert" data-testid="subscription-error">{error}</div>}
        <div style={{ marginTop: 8 }}>
          <button type="button" className="btn btn-sm btn-primary" onClick={() => void save()} data-testid="subscription-save">Save</button>{' '}
          <button type="button" className="btn btn-sm" onClick={props.onCancel} data-testid="subscription-cancel">Cancel</button>
        </div>
      </div>
    </div>
  )
}

// ---- history ----

function HistoryTab({ overview }: { overview: DeliveryOverview }) {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const [status, setStatus] = useState('')
  const [channel, setChannel] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  const list = useQuery({ queryKey: ['delivery', 'messages', status, channel], queryFn: () => deliveryApi.messages(status, channel), refetchInterval: 5000 })
  const requeue = useMutation({ mutationFn: (id: string) => deliveryApi.requeue(id), onSuccess: () => void client.invalidateQueries({ queryKey: ['delivery'] }) })
  const rows = list.data?.messages ?? []
  return (
    <div data-testid="delivery-history">
      <div className="row" style={{ gap: 8, marginBottom: 8 }}>
        <select className="input" value={status} onChange={(e) => setStatus(e.target.value)} data-testid="history-status"><option value="">every state</option><option value="pending">waiting</option><option value="sent">sent</option><option value="dead">failed</option></select>
        <select className="input" value={channel} onChange={(e) => setChannel(e.target.value)} data-testid="history-channel"><option value="">every channel</option>{overview.channels.map((c) => <option key={c.name} value={c.name}>{c.name}</option>)}</select>
        <span className="hint">{isAdmin ? 'Every message.' : 'The messages that were for you.'}</span>
      </div>
      <ErrorBanner error={list.error} />
      <ErrorBanner error={requeue.error} />
      {list.data && rows.length === 0 && <div className="empty" data-testid="history-empty">No message matches.</div>}
      {rows.length > 0 && (
        <table className="table" data-testid="history-table">
          <thead><tr><th>When</th><th>Channel</th><th>Subject</th><th>To</th><th>State</th><th>Tries</th><th>What the far side said</th><th /></tr></thead>
          <tbody>
            {rows.map((m) => <MessageRow key={m.id} m={m} open={open === m.id} onToggle={() => setOpen(open === m.id ? null : m.id)} canRequeue={isAdmin && m.status === 'dead'} onRequeue={() => requeue.mutate(m.id)} />)}
          </tbody>
        </table>
      )}
    </div>
  )
}

function MessageRow({ m, open, onToggle, canRequeue, onRequeue }: { m: DeliveryMessage; open: boolean; onToggle: () => void; canRequeue: boolean; onRequeue: () => void }) {
  const detail = useQuery({ queryKey: ['delivery', 'message', m.id, m.attempts], queryFn: () => deliveryApi.message(m.id), enabled: open })
  return (
    <>
      <tr data-testid="history-row" data-status={m.status} data-subject={m.subject}>
        <td>{m.createdAt}</td><td>{m.channel}</td>
        <td><button type="button" className="btn btn-sm" onClick={onToggle} aria-expanded={open} data-testid="history-open">{m.subject}</button>{m.attachments.length > 0 && <span className="hint"> · {m.attachments.map((a) => a.name).join(', ')}</span>}</td>
        <td>{m.recipients.join(', ')}</td>
        <td data-testid="history-state">{statusWord(m.status)}</td><td>{m.attempts}</td>
        <td data-testid="history-detail">{m.lastDetail}</td>
        <td>{canRequeue && <button type="button" className="btn btn-sm" onClick={onRequeue} data-testid="history-requeue">Send again</button>}</td>
      </tr>
      {open && (
        <tr data-testid="history-attempts">
          <td colSpan={8}>
            <ErrorBanner error={detail.error} />
            {detail.data && (
              <table className="table"><thead><tr><th>Try</th><th>Sender</th><th>Result</th><th>What was said</th><th>When</th></tr></thead>
                <tbody>{detail.data.attempts.map((a) => <tr key={a.number} data-testid="history-attempt" data-outcome={a.outcome}><td>{a.number}</td><td>{a.sender}</td><td>{a.outcome}</td><td>{a.detail}</td><td>{a.finishedAt}</td></tr>)}</tbody></table>
            )}
          </td>
        </tr>
      )}
    </>
  )
}
