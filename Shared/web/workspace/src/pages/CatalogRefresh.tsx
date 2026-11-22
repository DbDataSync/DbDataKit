import { useEffect, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link as ConsoleLink } from 'react-router-dom'
import { ErrorBanner, useModule } from '@dbdatakit/shell'
import {
  catalogReadApi, defaultChannel, differenceLine, isUnchanged, productionWarning, refreshOutcome, unboundLine, workspaceServerOf,
  type CatalogDifference, type CatalogReadAnswer, type CatalogReadRequest, type ChannelList, type RunnerChoice,
} from '../catalogRefresh'

/**
 * The channel picker of a read: the Runner and the channel to read a connection's shape from. The console lists the channels of the project's package on
 * the Runner and which bind the connection's slot; the first one that is not production and binds it is offered first. Picking production shows the
 * warning and the read still goes ahead. A channel that binds no slot is named with how to bind one.
 */
export function ChannelPicker({ runners, runner, onRunner, list, channel, onChannel, connection }: {
  runners: readonly RunnerChoice[]; runner: string; onRunner: (id: string) => void
  list: ChannelList | undefined; channel: string; onChannel: (channel: string) => void; connection: string
}) {
  const chosen = list?.channels.find((c) => c.channel === channel)
  const runnerName = runners.find((r) => r.id === runner)?.name ?? runner
  return (
    <div data-testid="wsp-channel-picker" data-runner={runner} data-channel={channel}>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <label className="hint" htmlFor="wsp-channel-runner">Runner</label>
        <select id="wsp-channel-runner" className="input" value={runner} onChange={(e) => onRunner(e.target.value)} data-testid="wsp-channel-runner">
          {runners.length === 0 && <option value="">no Runner is paired</option>}
          {runners.map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
        </select>
        <label className="hint" htmlFor="wsp-channel-channel">Channel</label>
        <select id="wsp-channel-channel" className="input" value={channel} onChange={(e) => onChannel(e.target.value)} data-testid="wsp-channel-channel">
          <option value="">choose a channel</option>
          {(list?.channels ?? []).map((c) => (
            <option key={c.channel} value={c.channel} disabled={!c.bound} data-bound={c.bound}>
              {c.channel}{c.production ? ' (production)' : ''}{c.bound ? `: ${connection} is ${c.bound_to}` : ': not bound'}
            </option>
          ))}
        </select>
      </div>
      {list?.note && <div className="hint" data-testid="wsp-channel-note">{list.note}</div>}
      {(list?.channels ?? []).filter((c) => !c.bound).map((c) => <div key={c.channel} className="hint" data-testid="wsp-channel-unbound" data-channel={c.channel}>{unboundLine(c, list!.slot)}</div>)}
      {chosen?.production && (
        <div className="banner" role="alert" style={{ marginTop: 6 }} data-testid="wsp-channel-production"><span className="mark">!</span><span>{productionWarning(connection, chosen.channel, runnerName)}</span></div>
      )}
    </div>
  )
}

/** The difference a read found, before the cache is replaced: each table and column added, removed or retyped, with Accept and Dismiss. */
export function RefreshDifference({ difference, onAccept, onDismiss, canEdit }: { difference: CatalogDifference; onAccept: () => void; onDismiss: () => void; canEdit: boolean }) {
  const unchanged = isUnchanged(difference)
  return (
    <div className="callout" style={{ marginTop: 8 }} data-testid="wsp-refresh-difference" data-unchanged={unchanged}>
      <div data-testid="wsp-refresh-summary">{unchanged ? 'No change since the cache was read.' : differenceLine(difference)}</div>
      <ul style={{ margin: '6px 0' }}>
        {difference.tablesAdded.map((t) => <li key={`ta${t}`} data-testid="wsp-refresh-change" data-change="table-added"><span className="mono">{t}</span> added</li>)}
        {difference.tablesRemoved.map((t) => <li key={`tr${t}`} data-testid="wsp-refresh-change" data-change="table-removed"><span className="mono">{t}</span> removed</li>)}
        {difference.columnsAdded.map((c) => <li key={`ca${c.table}.${c.column}`} data-testid="wsp-refresh-change" data-change="column-added"><span className="mono">{c.table}.{c.column}</span> added ({c.to})</li>)}
        {difference.columnsRemoved.map((c) => <li key={`cr${c.table}.${c.column}`} data-testid="wsp-refresh-change" data-change="column-removed"><span className="mono">{c.table}.{c.column}</span> removed</li>)}
        {difference.columnsRetyped.map((c) => <li key={`ct${c.table}.${c.column}`} data-testid="wsp-refresh-change" data-change="column-retyped"><span className="mono">{c.table}.{c.column}</span> {c.from} to {c.to}</li>)}
      </ul>
      <div className="row" style={{ gap: 8 }}>
        <button type="button" className="btn btn-primary" disabled={!canEdit} onClick={onAccept} data-testid="wsp-refresh-accept">{unchanged ? 'Record the new read' : 'Accept and replace the cache'}</button>
        <button type="button" className="btn" onClick={onDismiss} data-testid="wsp-refresh-dismiss">Dismiss</button>
      </div>
    </div>
  )
}

/** How often the screen asks how a read stands once the console stopped waiting. */
const FOLLOW_MS = 2000

/**
 * **Refresh** of a connection, or of one table of it, from a Runner channel: the console starts the Runner's `discover` as the signed-in person and waits
 * up to 10 seconds; after that the screen shows the run's link and follows the run. The difference with the cache the screen holds (`current`, unsaved
 * edits included) is shown before anything is replaced; Accept hands the new cache text to `onAccept`, which puts it in the draft as an unsaved edit.
 */
export function CatalogRefresh({ project, connection, table, current, onAccept, canEdit }: {
  project: string; connection: string; table?: { schema: string; name: string }; current: string | undefined; onAccept: (text: string) => void; canEdit: boolean
}) {
  const workspace = workspaceServerOf(useModule().apiBase)
  const runners = useQuery({ queryKey: ['console', 'runners'], queryFn: catalogReadApi.runners, enabled: workspace !== null, retry: false })
  const [runner, setRunner] = useState('')
  const firstRunner = runners.data?.[0]?.id ?? ''
  const runnerId = runner || firstRunner
  const channels = useQuery({
    queryKey: ['console', 'catalog-channels', workspace, project, runnerId, connection], queryFn: () => catalogReadApi.channels(workspace!, project, runnerId, connection),
    enabled: workspace !== null && runnerId !== '' && connection !== '', retry: false,
  })
  const [channel, setChannel] = useState('')
  const offered = defaultChannel(channels.data)
  useEffect(() => { setChannel('') }, [runnerId, connection])
  const channelChosen = channel || offered
  const [asked, setAsked] = useState<CatalogReadRequest | null>(null)
  const [dismissed, setDismissed] = useState<string | null>(null)

  const start = useMutation<CatalogReadAnswer, Error, CatalogReadRequest>({ mutationFn: (body) => catalogReadApi.read(workspace!, project, body), onMutate: (body) => { setAsked(body); setDismissed(null) } })
  const running = start.data?.state === 'running' ? start.data : undefined
  const follow = useQuery({
    queryKey: ['console', 'catalog-read', workspace, project, running?.run_id], queryFn: () => catalogReadApi.follow(workspace!, project, running!.run_id, asked!),
    enabled: running !== undefined && asked !== null, refetchInterval: (q) => (q.state.data?.state === 'running' || q.state.data === undefined ? FOLLOW_MS : false), retry: false,
  })
  const answer = start.data?.state === 'done' ? start.data : follow.data?.state === 'done' ? follow.data : undefined
  const outcome = answer?.cache !== undefined && dismissed !== answer.run_id ? refreshOutcome(current, answer.cache, connection, table) : undefined

  if (workspace === null) return <div className="hint" data-testid="wsp-refresh-unavailable">A read from a Runner is asked through the console: open this draft from the console's Workspace screens.</div>
  const what = table ? `${connection} ${table.schema}.${table.name}` : connection
  const body: CatalogReadRequest = { runner: runnerId, channel: channelChosen, connection, ...(table ? { schema: table.schema, table: table.name } : {}) }
  const stillRunning = running !== undefined && follow.data?.state !== 'done' && !follow.isError
  return (
    <div data-testid="wsp-refresh" data-connection={connection} data-table={table ? `${table.schema}.${table.name}` : ''}>
      <ChannelPicker runners={runners.data ?? []} runner={runnerId} onRunner={setRunner} list={channels.data} channel={channelChosen} onChannel={setChannel} connection={connection} />
      <ErrorBanner error={runners.error ?? channels.error} />
      <button type="button" className="btn" style={{ marginTop: 6 }} disabled={!canEdit || runnerId === '' || channelChosen === '' || start.isPending || stillRunning} onClick={() => start.mutate(body)} data-testid="wsp-refresh-go">
        Refresh {what}
      </button>
      {start.isPending && <div className="hint" data-testid="wsp-refresh-waiting">Reading the shape of {what} in {channelChosen}...</div>}
      {stillRunning && running && (
        <div className="hint" data-testid="wsp-refresh-running">
          The read takes longer than 10 seconds. It goes on as <ConsoleLink to={running.link} data-testid="wsp-refresh-run-link">a run on {running.runner_name}</ConsoleLink>, and its answer is shown here when it ends.
        </div>
      )}
      {start.isError && <div data-testid="wsp-refresh-failed"><ErrorBanner error={start.error} /></div>}
      {follow.isError && <div data-testid="wsp-refresh-failed"><ErrorBanner error={follow.error} /></div>}
      {outcome && !outcome.ok && <div className="banner error" role="alert" data-testid="wsp-refresh-refused"><span className="mark">!</span><span>{outcome.problem}</span></div>}
      {outcome?.ok && answer && (
        <RefreshDifference difference={outcome.difference} canEdit={canEdit}
          onAccept={() => { onAccept(outcome.text); setDismissed(answer.run_id) }} onDismiss={() => setDismissed(answer.run_id)} />
      )}
    </div>
  )
}
