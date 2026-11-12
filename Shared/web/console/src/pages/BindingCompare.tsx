import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { consoleApi, instancesKey } from '../api'
import {
  STATE_WORDS, adoptWords, compareApi, decisionOf, differing, heldWords, itemTitle, publishWords, summary,
  type CompareItem, type CompareOutcome,
} from '../bindingCompare'

const EMBEDDED = 'console-runner'
const COMPARABLE = new Set(['dbdatarunner', 'dbdatagateway'])

/**
 * Compare, adopt and publish (plan 27a IX25, docs/channel-bindings.md): the console's bindings and connections beside a Runner's or a Gateway's, what differs marked, and per item **Adopt** (the other
 * side's value becomes the console's own file) or **Publish** (the console's value goes to the other server by plan 30's shared route, as the signed-in person). Anyone signed in reads; an administrator
 * decides. The server compares and refuses a decision taken on a value that changed since it was shown.
 */
export function BindingComparePanel() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const servers = useQuery({ queryKey: instancesKey, queryFn: consoleApi.instances.list })
  const options = [{ id: EMBEDDED, name: "The console's runner" }, ...(servers.data ?? []).filter((s) => s.product !== null && COMPARABLE.has(s.product) && s.reachable).map((s) => ({ id: s.id, name: `${s.name} (${s.product === 'dbdatagateway' ? 'Gateway' : 'Runner'})` }))]
  const [server, setServer] = useState(EMBEDDED)
  const [onlyDifferences, setOnlyDifferences] = useState(true)
  const view = useQuery({ queryKey: ['bindings', 'compare', server], queryFn: () => compareApi.compare(server), retry: false })
  const [said, setSaid] = useState<Record<string, string>>({})
  const done = (what: string) => (r: CompareOutcome, item: CompareItem) => {
    setSaid((s) => ({ ...s, [item.id]: what }))
    void client.invalidateQueries({ queryKey: ['bindings'] })
    void r
  }
  const failed = (item: CompareItem) => (e: unknown) => {
    const body = (e as { body?: { error?: string } } | null)?.body
    setSaid((s) => ({ ...s, [item.id]: body?.error ?? String((e as Error)?.message ?? 'It was refused.') }))
    void client.invalidateQueries({ queryKey: ['bindings', 'compare'] })
  }
  const adopt = useMutation({ mutationFn: (item: CompareItem) => compareApi.adopt(decisionOf(server, item)), onSuccess: (r, item) => done('Adopted: the console now holds the value.')(r, item), onError: (e, item) => failed(item)(e) })
  const publish = useMutation({ mutationFn: (item: CompareItem) => compareApi.publish(decisionOf(server, item)), onSuccess: (r, item) => done(`Published to ${view.data?.serverName ?? 'the server'}.`)(r, item), onError: (e, item) => failed(item)(e) })
  const items = view.data?.items ?? []
  const shown = onlyDifferences ? differing(items) : items
  return (
    <section data-testid="compare-panel" style={{ marginTop: 24 }}>
      <h2>Compare with a Runner or a Gateway</h2>
      <p className="hint" data-testid="compare-rule">
        What the console holds beside what another server holds for the same thing. <strong>Adopt</strong> takes the other server&apos;s value into the console; <strong>Publish</strong> sends the console&apos;s value to it
        (the same signed route as every other publish, as you). Each side keeps enforcing its own rules: a production connection in a non-production release channel is still confirmed where it is bound.
      </p>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', margin: '8px 0' }}>
        <select className="input" value={server} onChange={(e) => setServer(e.target.value)} aria-label="Server to compare with" data-testid="compare-server">
          {options.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
        </select>
        <label className="row" style={{ gap: 4, alignItems: 'center' }}>
          <input type="checkbox" checked={onlyDifferences} onChange={(e) => setOnlyDifferences(e.target.checked)} data-testid="compare-only-differences" /> Only what differs
        </label>
        <button type="button" className="btn btn-sm" onClick={() => void view.refetch()} data-testid="compare-refresh">Compare again</button>
        {view.data && <span className="hint" data-testid="compare-summary">{summary(items)}</span>}
      </div>
      <ErrorBanner error={view.error} />
      {view.data?.problems.map((p, i) => <div key={i} className="banner" data-testid="compare-problem">{p}</div>)}
      {view.isSuccess && shown.length === 0 && <div className="empty" data-testid="compare-empty">{items.length === 0 ? 'Nothing here is held by both sides.' : 'Everything is the same.'}</div>}
      {Object.entries(said).map(([id, text]) => <div key={id} className="hint" data-testid="compare-said" data-item={id}>{items.find((i) => i.id === id) ? itemTitle(items.find((i) => i.id === id)!) : id}: {text}</div>)}
      {shown.length > 0 && (
        <table className="table" style={{ width: '100%' }} data-testid="compare-table">
          <thead><tr><th>Item</th><th>State</th><th>Console</th><th>{view.data?.serverName}</th><th /></tr></thead>
          <tbody>
            {shown.map((i) => (
              <tr key={i.id} data-testid="compare-item" data-item={i.id} data-state={i.state}>
                <td><span className="mono">{itemTitle(i)}</span> <span className="badge">{i.family}</span></td>
                <td data-testid="compare-state">{STATE_WORDS[i.state]}{i.problem ? <div className="hint">{i.problem}</div> : null}</td>
                <td className="mono" data-testid="compare-here">{i.here ?? '—'}</td>
                <td className="mono" data-testid="compare-there">{i.there ?? '—'}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  {isAdmin && i.canAdopt && <button type="button" className="btn btn-sm" disabled={adopt.isPending} onClick={() => adopt.mutate(i)} data-testid="compare-adopt" title={adoptWords(i, view.data?.serverName ?? 'the server')}>Adopt</button>}{' '}
                  {isAdmin && i.canPublish && <button type="button" className="btn btn-sm" disabled={publish.isPending} onClick={() => publish.mutate(i)} data-testid="compare-publish" title={publishWords(i, view.data?.serverName ?? 'the server')}>Publish</button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}

/**
 * The posture choices of the console's table (`bindings.posture_choice`) and of the Runners' (`runner.posture_choice`) as one read-only list (plan 27a IX25). A choice is listed once whichever table holds it
 * and the holders are named; each table is still the one that enforces its own binding, so nothing is changed here.
 */
export function PostureListPanel() {
  const posture = useQuery({ queryKey: ['bindings', 'posture'], queryFn: compareApi.posture, retry: false })
  const [open, setOpen] = useState<string | null>(null)
  return (
    <section data-testid="posture-panel" style={{ marginTop: 24 }}>
      <h2>Posture choices</h2>
      <p className="hint" data-testid="posture-rule">
        Every default that was loosened on purpose, by the console and by the Runners, in one list. Read only: each server keeps and enforces the choices of its own bindings.
      </p>
      <ErrorBanner error={posture.error} />
      {posture.data?.sources.filter((s) => !s.ok).map((s) => <div key={s.holder} className="banner" data-testid="posture-source-problem">{s.holder} could not be read: {s.problem}</div>)}
      {posture.isSuccess && posture.data.entries.length === 0 && <div className="empty" data-testid="posture-empty">No default has been loosened.</div>}
      {posture.data && posture.data.entries.length > 0 && (
        <table className="table" style={{ width: '100%' }} data-testid="posture-table">
          <thead><tr><th>Setting</th><th>For</th><th>Chosen by</th><th>Held in</th><th /></tr></thead>
          <tbody>
            {posture.data.entries.map((e) => {
              const key = `${e.setting}|${e.scope}`
              return (
                <tr key={key} data-testid="posture-entry" data-scope={e.scope} data-setting={e.setting}>
                  <td className="mono">{e.setting}</td>
                  <td className="mono">{e.scope}</td>
                  <td>{e.chosenBy}, {new Date(e.chosenAt).toLocaleString()}{e.note ? <div className="hint">{e.note}</div> : null}</td>
                  <td data-testid="posture-held">{heldWords(e)}</td>
                  <td>{e.effect && <button type="button" className="btn btn-sm" onClick={() => setOpen(open === key ? null : key)} data-testid="posture-effect-button">{open === key ? 'Hide' : 'What it allows'}</button>}
                    {open === key && <div className="hint" data-testid="posture-effect">{e.effect}</div>}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}
    </section>
  )
}
