import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CodeEditor, ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { ConsoleShell } from '../ConsoleShell'
import { agentsApi, type Diagnostic } from '../agents'
import { deliveryApi } from '../delivery'
import {
  KIND_LABEL, bindingsApi, connectionsFor, draftOf, draftProblem, isReleaseName, isSlotName, loadRunnerSlots, maxChoices, moveChoice, needsConfirmation, needsModel, orderedReleases, rowLine, runnerApi, sameDraft, slotBody, unboundWords,
  type BindingConnection, type BindingEntry, type BindingSlot, type BindingsView, type SlotDraft, type SlotKind, type Unconfirmed,
} from '../bindings'

/**
 * Bindings (plan 27a IX23, docs/channel-bindings.md): every name the suite binds to a connection per release channel, in one place and one shape. A slot is a chat model, an embedding model, a delivery channel or a runner package's connection slot; per release channel it
 * has the connection (or an ordered list, for a model's fallback), whether it can be used now, a Test, and for an administrator the change. One rule for all: a release channel's own entry, else the `*` entry written on purpose, else none (never another release channel's).
 * A delivery channel that would use production's connection in a test release channel is an explicit, confirmed, recorded choice. The Models and Delivery pages show this same panel for their kinds; there is no second editor.
 */
export function BindingsPage() {
  return (
    <ConsoleShell crumbs={[{ label: 'Bindings', heading: true }]}>
      <div className="pane" data-testid="bindings-page">
        <div className="page-head"><h1 className="page-title">Bindings</h1><span className="page-note">Which connection each model, delivery channel and runner slot uses in each release channel.</span></div>
        <BindingsPanel />
      </div>
    </ConsoleShell>
  )
}

type Tested = { ok: boolean; text: string }

export function BindingsPanel({ kinds, showRunner = true, showFile = true }: { kinds?: SlotKind[]; showRunner?: boolean; showFile?: boolean }) {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const view = useQuery({ queryKey: ['bindings'], queryFn: bindingsApi.get })
  const wantRunner = showRunner && (kinds === undefined || kinds.includes('runner'))
  const runner = useQuery({ queryKey: ['bindings', 'runner'], queryFn: () => loadRunnerSlots(), enabled: wantRunner, retry: false })
  const [adding, setAdding] = useState<{ kind: SlotKind; name: string } | null>(null)
  const [newKind, setNewKind] = useState<SlotKind>(kinds?.[0] && kinds[0] !== 'runner' ? kinds[0] : 'model-chat')
  const [newName, setNewName] = useState('')
  const refresh = () => { void client.invalidateQueries({ queryKey: ['bindings'] }); void client.invalidateQueries({ queryKey: ['agents'] }); void client.invalidateQueries({ queryKey: ['delivery'] }) }
  const data = view.data
  const shown = useMemo(() => [...(data?.slots ?? []), ...(runner.data ?? [])].filter((s) => kinds === undefined || kinds.includes(s.kind)), [data, runner.data, kinds])
  const addable = (kinds ?? ['model-chat', 'model-embeddings', 'delivery']).filter((k) => k !== 'runner')
  return (
    <div data-testid="bindings-panel">
      <ErrorBanner error={view.error} />
      {data?.earlier && <div className="banner error" data-testid="bindings-earlier">{data.earlier}</div>}
      {data && data.diagnostics.length > 0 && (
        <div data-testid="bindings-diagnostics">{data.diagnostics.map((d, i) => <div key={i} className={d.severity === 'error' ? 'banner error' : 'banner'} data-testid="bindings-diagnostic" data-severity={d.severity}><span className="mono">{d.code}</span> line {d.line}: {d.message}{d.fix ? ` ${d.fix}` : ''}</div>)}</div>
      )}
      <p className="hint" data-testid="bindings-rule">
        A slot is bound to a connection <strong>per release channel</strong> ({(data?.releaseChannels ?? ['prod']).join(', ')}). A release channel with no entry has nothing bound: it never uses another&apos;s connection, so a test cannot reach production&apos;s.
        <span className="mono"> *</span> is an entry written on purpose for every release channel without its own. Connections are kept on the <a href="/connections">Connections page</a>.
      </p>
      {shown.map((s) => <SlotCard key={`${s.kind}/${s.name}`} slot={s} view={data} isAdmin={isAdmin} onSaved={refresh} />)}
      {shown.length === 0 && view.isSuccess && <div className="empty" data-testid="bindings-empty">Nothing is bound yet. {isAdmin ? 'Add a slot below and choose its connection.' : 'An administrator binds a slot to a connection.'}</div>}
      {wantRunner && runner.isError && <div className="hint" data-testid="bindings-runner-unreadable">The runner&apos;s slots could not be read now ({String((runner.error as Error)?.message ?? 'no answer')}).</div>}
      {isAdmin && addable.length > 0 && !adding && (
        <form className="row" style={{ gap: 8, alignItems: 'flex-end', flexWrap: 'wrap', margin: '12px 0' }} data-testid="binding-add"
          onSubmit={(e) => { e.preventDefault(); if (isSlotName(newName) && !shown.some((s) => s.kind === newKind && s.name === newName)) { setAdding({ kind: newKind, name: newName }); setNewName('') } }}>
          <select className="input" value={newKind} onChange={(e) => setNewKind(e.target.value as SlotKind)} aria-label="Kind of slot" data-testid="binding-add-kind">{addable.map((k) => <option key={k} value={k}>{KIND_LABEL[k]}</option>)}</select>
          <input className="input mono" placeholder={newKind === 'delivery' ? "a channel's name, such as alerts" : "a slot's name, such as default"} value={newName} onChange={(e) => setNewName(e.target.value)} data-testid="binding-add-name" />
          <button type="submit" className="btn btn-sm" disabled={!isSlotName(newName)} data-testid="binding-add-button">Bind a new slot</button>
        </form>
      )}
      {adding && data && <SlotCard slot={{ section: adding.kind === 'delivery' ? 'delivery' : 'models', kind: adding.kind, name: adding.name, usedBy: [], limitPerMinute: null, rows: [] }} view={data} isAdmin startEditing onSaved={() => { setAdding(null); refresh() }} onCancel={() => setAdding(null)} />}
      {showFile && data && isAdmin && <FileEditor view={data} onSaved={refresh} />}
    </div>
  )
}

function SlotCard({ slot, view, isAdmin, onSaved, startEditing = false, onCancel }: { slot: BindingSlot; view: BindingsView | undefined; isAdmin: boolean; onSaved: () => void; startEditing?: boolean; onCancel?: () => void }) {
  const [draft, setDraft] = useState<SlotDraft | null>(startEditing ? {} : null)
  const [newRelease, setNewRelease] = useState('')
  const [confirm, setConfirm] = useState<Unconfirmed[] | null>(null)
  const [note, setNote] = useState('')
  const [recipient, setRecipient] = useState('')
  const [tested, setTested] = useState<Record<string, Tested>>({})
  const saved = useMemo(() => draftOf(slot), [slot])
  const editing = draft !== null
  const dirty = editing && !sameDraft(draft, saved)
  const problem = editing ? draftProblem(slot.kind, draft) : null
  const connections = connectionsFor(slot.kind, view?.connections ?? [])
  const editable = slot.kind !== 'runner' && isAdmin
  const asks = editing ? needsConfirmation(slot, draft) : []
  const save = useMutation({
    mutationFn: (confirmed: string[]) => bindingsApi.setSlot(slot.section, slot.name, slotBody(slot.kind, draft ?? {}, confirmed, note || undefined)),
    onSuccess: () => { setDraft(null); setConfirm(null); setNote(''); onSaved() },
    onError: (e) => { const needs = (e as { body?: { needsConfirmation?: Unconfirmed[] } } | null)?.body?.needsConfirmation; if (needs?.length) setConfirm(needs) },
  })
  const remove = useMutation({ mutationFn: () => bindingsApi.removeSlot(slot.section, slot.name), onSuccess: onSaved })
  const test = useMutation({
    mutationFn: async (v: { release: string; entry: BindingEntry }): Promise<[string, Tested]> => {
      const key = `${v.release}/${v.entry.connection}`
      if (slot.kind === 'delivery') {
        const a = await deliveryApi.test(slot.name, recipient, v.release)
        return [key, { ok: a.message.status === 'sent', text: `through ${a.connection}: ${a.message.status}${a.message.lastDetail ? `: ${a.message.lastDetail}` : ''}` }]
      }
      if (slot.kind === 'runner' && slot.runner) {
        const a = await runnerApi.test(v.release, slot.runner.package, slot.name.split('/').slice(1).join('/'))
        return [key, { ok: a.ok, text: a.message }]
      }
      const a = await agentsApi.testProvider(v.entry.connection, v.entry.model ?? '')
      return [key, { ok: a.ok, text: a.message }]
    },
    onSuccess: ([key, r]) => setTested((t) => ({ ...t, [key]: r })),
  })
  const releases = orderedReleases(editing ? Object.keys(draft) : slot.rows.map((r) => r.release))
  const change = (release: string, list: SlotDraft[string]) => setDraft({ ...(draft ?? {}), [release]: list })
  const modelsOf = (name: string) => connections.find((c) => c.name === name)?.models ?? []
  const first = (): { connection: string; model: string } => { const c = connections[0]; return { connection: c?.name ?? '', model: needsModel(slot.kind) ? c?.models[0] ?? '' : '' } }
  const rowOf = (release: string) => slot.rows.find((r) => r.release === release)
  const submit = () => { if (asks.length > 0 && !confirm) { setConfirm(asks.map((a) => ({ scope: a.scope, section: slot.section, slot: slot.name, release: a.release, connection: a.connection, effect: view?.postureEffect ?? '' }))); return } save.mutate((confirm ?? []).map((c) => c.scope)) }
  return (
    <div className="card" style={{ marginBottom: 12 }} data-testid="binding-slot" data-slot={slot.name} data-kind={slot.kind}>
      <div className="card-body">
        <strong className="mono">{slot.name}</strong> <span className="badge">{KIND_LABEL[slot.kind]}</span>
        {slot.usedBy.length > 0 && <span className="hint"> asked for by {slot.usedBy.join(', ')}</span>}
        {slot.limitPerMinute ? <span className="hint">, at most {slot.limitPerMinute} a minute</span> : null}
        <table className="table" style={{ margin: '8px 0', width: '100%' }} data-testid={`binding-rows-${slot.name}`}>
          <thead><tr><th>Release channel</th><th>{needsModel(slot.kind) ? 'Connection and model (the first is used, the others take over)' : 'Connection'}</th><th>State</th><th /></tr></thead>
          <tbody>
            {releases.map((release) => {
              const row = rowOf(release)
              const list = editing ? draft[release] ?? [] : []
              return (
                <tr key={release} data-testid={`binding-row-${slot.name}-${release}`} data-release={release} data-line={row ? rowLine(row) : ''}>
                  <td className="mono">{release === '*' ? '* (every other)' : release}</td>
                  <td>
                    {!editing && row?.entries.map((e, i) => <div key={i}><span className="mono">{e.connection}{e.model ? `/${e.model}` : ''}</span>{e.target ? <span className="hint"> reaches {e.target}</span> : null}</div>)}
                    {editing && (
                      <ol style={{ margin: 0, paddingLeft: 18 }}>
                        {list.map((c, i) => (
                          <li key={i} data-testid="binding-choice">
                            <select className="input" value={c.connection} aria-label={`Connection for ${release}`} data-testid={`binding-select-${slot.name}-${release}-${i}`}
                              onChange={(e) => change(release, list.map((x, j) => (j === i ? { connection: e.target.value, model: needsModel(slot.kind) ? modelsOf(e.target.value)[0] ?? '' : '' } : x)))}>
                              {!connections.some((x) => x.name === c.connection) && <option value={c.connection}>{c.connection || 'choose'}</option>}
                              {connections.map((x: BindingConnection) => <option key={x.name} value={x.name}>{x.name}{x.cloud === true ? ' (cloud)' : x.cloud === false ? ' (local)' : ''}</option>)}
                            </select>{' '}
                            {needsModel(slot.kind) && (
                              <select className="input" value={c.model} aria-label={`Model for ${release}`} data-testid={`binding-model-${slot.name}-${release}-${i}`} onChange={(e) => change(release, list.map((x, j) => (j === i ? { ...x, model: e.target.value } : x)))}>
                                {c.model !== '' && !modelsOf(c.connection).includes(c.model) && <option value={c.model}>{c.model}</option>}
                                {modelsOf(c.connection).map((m) => <option key={m} value={m}>{m}</option>)}
                              </select>
                            )}{' '}
                            {maxChoices(slot.kind) > 1 && <><button type="button" className="btn btn-sm" disabled={i === 0} onClick={() => change(release, moveChoice(list, i, -1))} data-testid={`binding-up-${slot.name}-${release}-${i}`}>Up</button>{' '}
                              <button type="button" className="btn btn-sm" disabled={i === list.length - 1} onClick={() => change(release, moveChoice(list, i, 1))} data-testid={`binding-down-${slot.name}-${release}-${i}`}>Down</button>{' '}</>}
                            {maxChoices(slot.kind) > 1 && <button type="button" className="btn btn-sm" onClick={() => change(release, list.filter((_, j) => j !== i))} data-testid={`binding-choice-remove-${slot.name}-${release}-${i}`}>Remove</button>}
                          </li>
                        ))}
                        {list.length < maxChoices(slot.kind) && connections.length > 0 && <li className="hint"><button type="button" className="btn btn-sm" onClick={() => change(release, [...list, first()])} data-testid={`binding-add-choice-${slot.name}-${release}`}>{list.length === 0 ? 'Choose a connection' : 'Add a fallback'}</button></li>}
                      </ol>
                    )}
                  </td>
                  <td data-testid={`binding-state-${slot.name}-${release}`}>
                    {!editing && row && row.entries.length > 0 && row.entries.every((e) => e.usable) && <span className="hint">ready</span>}
                    {!editing && row?.entries.filter((e) => !e.usable).map((e, i) => <div key={i} className="error">{e.connection}: {e.problem ?? 'cannot be used'}</div>)}
                    {!editing && row && row.entries.length === 0 && <span className="error">not bound ({unboundWords(slot, release)})</span>}
                    {!editing && row?.needsConfirmation && <div className={row.confirmed ? 'hint' : 'error'} data-testid={`binding-posture-${slot.name}-${release}`}>{row.confirmed ? 'Production\'s connection serves this release channel by a recorded choice.' : 'Production\'s connection serves this release channel and nobody confirmed it.'}</div>}
                    {editing && dirty && <span className="hint">not saved yet</span>}
                  </td>
                  <td className="row" style={{ gap: 6 }}>
                    {!editing && row?.entries.filter((e) => e.usable).slice(0, 1).map((e) => (
                      <button key={e.connection} type="button" className="btn btn-sm" disabled={!isAdmin || test.isPending || release === '*'} title={release === '*' ? 'Test through a release channel that names its own entry' : 'Calls the connection and says what came back'} onClick={() => test.mutate({ release, entry: e })} data-testid={`binding-test-${slot.name}-${release}`}>
                        {test.isPending && test.variables?.release === release ? 'Testing…' : 'Test'}
                      </button>
                    ))}
                    {editing && <button type="button" className="btn btn-sm" onClick={() => { const { [release]: _gone, ...rest } = draft; setDraft(rest) }} data-testid={`binding-release-remove-${slot.name}-${release}`}>Remove</button>}
                    {!editing && row && Object.entries(tested).filter(([k]) => k.startsWith(`${release}/`)).map(([k, t]) => <span key={k} className={t.ok ? 'hint' : 'error'} role="status" data-testid={`binding-test-result-${slot.name}-${release}`}>{t.text}</span>)}
                  </td>
                </tr>
              )
            })}
            {releases.length === 0 && <tr><td colSpan={4} className="hint" data-testid={`binding-none-${slot.name}`}>Bound in no release channel: nothing can use this name.</td></tr>}
          </tbody>
        </table>
        {slot.kind === 'delivery' && isAdmin && !editing && <input className="input" style={{ marginBottom: 8 }} placeholder="test mail goes to (default: your address)" value={recipient} onChange={(e) => setRecipient(e.target.value)} data-testid={`binding-test-to-${slot.name}`} />}
        {slot.kind === 'runner' && <div className="hint">The runner keeps these (package/1): it binds a slot per package and release channel and never inherits. Change them on the runner&apos;s own screen.</div>}
        {editable && !editing && (
          <div className="row" style={{ gap: 8 }}>
            <button type="button" className="btn btn-sm" onClick={() => setDraft(saved)} data-testid={`binding-edit-${slot.name}`}>Change</button>
            <button type="button" className="btn btn-sm" onClick={() => remove.mutate()} data-testid={`binding-remove-${slot.name}`}>Remove the slot</button>
          </div>
        )}
        {editing && (
          <div data-testid={`binding-editor-${slot.name}`}>
            <form className="row" style={{ gap: 8, alignItems: 'center' }} onSubmit={(e) => { e.preventDefault(); if (isReleaseName(newRelease) && !(newRelease in draft)) { setDraft({ ...draft, [newRelease]: connections[0] ? [first()] : [] }); setNewRelease('') } }}>
              <input className="input mono" placeholder="bind a release channel: prod, test, dev, or * for every other" value={newRelease} onChange={(e) => setNewRelease(e.target.value)} data-testid={`binding-new-release-${slot.name}`} />
              <button type="submit" className="btn btn-sm" disabled={!isReleaseName(newRelease) || newRelease in draft} data-testid={`binding-new-release-add-${slot.name}`}>Bind</button>
            </form>
            {confirm && (
              <div className="banner error" style={{ margin: '8px 0' }} data-testid={`binding-confirm-${slot.name}`}>
                <strong>Confirm: production&apos;s connection in a non-production release channel.</strong>
                {confirm.map((c) => <div key={c.scope} data-testid="binding-confirm-item" data-scope={c.scope}>{c.effect || `${c.release} would use ${c.connection}, which production uses.`}</div>)}
                <input className="input" placeholder="why (kept with the choice)" value={note} onChange={(e) => setNote(e.target.value)} data-testid={`binding-confirm-note-${slot.name}`} style={{ marginTop: 6 }} />
              </div>
            )}
            <div className="row" style={{ gap: 8, margin: '8px 0', alignItems: 'center' }}>
              <button type="button" className="btn btn-sm btn-primary" disabled={!dirty || problem !== null || save.isPending} onClick={submit} data-testid={`binding-save-${slot.name}`}>{save.isPending ? 'Saving…' : confirm ? 'Confirm and save' : 'Save'}</button>
              <button type="button" className="btn btn-sm" onClick={() => { setDraft(null); setConfirm(null); save.reset(); onCancel?.() }} data-testid={`binding-discard-${slot.name}`}>Discard changes</button>
              {problem && <span className="error" data-testid={`binding-problem-${slot.name}`}>{problem}</span>}
            </div>
            {(save.error as { body?: { diagnostics?: Diagnostic[] } } | null)?.body?.diagnostics?.map((d, i) => <div key={i} className="banner error" data-testid="binding-diagnostic">line {d.line}: {d.message}</div>)}
          </div>
        )}
        <ErrorBanner error={remove.error ?? test.error} />
      </div>
    </div>
  )
}

/** The file itself, for a person who prefers to edit text: read by the same strict reader, checked against the connections, comments kept. */
function FileEditor({ view, onSaved }: { view: BindingsView; onSaved: () => void }) {
  const [open, setOpen] = useState(false)
  const [yaml, setYaml] = useState<string | null>(null)
  const [problems, setProblems] = useState<Diagnostic[]>([])
  const [needs, setNeeds] = useState<Unconfirmed[]>([])
  const text = yaml ?? (view.yaml || 'bindings: 1\nmodels:\n  default:\n    prod:\n      - { connection: <connection>, model: <model> }\ndelivery:\n  alerts: { prod: <connection>, test: <connection> }\n')
  const check = useMutation({ mutationFn: () => bindingsApi.check(text), onSuccess: (r) => { setProblems(r.diagnostics); setNeeds(r.needsConfirmation) } })
  const save = useMutation({
    mutationFn: (confirm: string[]) => bindingsApi.save(text, confirm),
    onSuccess: (r) => { setProblems(r.diagnostics); setNeeds([]); setYaml(null); onSaved() },
    onError: (e) => { const b = (e as { body?: { diagnostics?: Diagnostic[]; needsConfirmation?: Unconfirmed[] } } | null)?.body; setProblems(b?.diagnostics ?? []); setNeeds(b?.needsConfirmation ?? []) },
  })
  return (
    <div style={{ marginTop: 16 }} data-testid="bindings-file">
      <button type="button" className="btn btn-sm" onClick={() => setOpen(!open)} data-testid="bindings-file-toggle">{open ? 'Hide' : 'Edit'} {view.file} as text</button>
      {open && (
        <div>
          <CodeEditor value={text} language="yaml" onChange={(v) => { setYaml(v); setProblems([]); setNeeds([]) }} diagnostics={problems.map((d) => ({ line: d.line, column: d.column, message: d.message, severity: d.severity === 'error' ? 'error' as const : 'warning' as const }))} testId="bindings-yaml" />
          {needs.map((n) => <div key={n.scope} className="banner error" data-testid="bindings-file-confirm" data-scope={n.scope}>{n.effect}</div>)}
          <div data-testid="bindings-file-diagnostics">{problems.map((d, i) => <div key={i} data-testid="bindings-file-diagnostic"><span className="mono">{d.code}</span> line {d.line}: {d.message}{d.fix ? ` ${d.fix}` : ''}</div>)}</div>
          <div style={{ margin: '8px 0' }}>
            <button type="button" className="btn btn-sm" onClick={() => check.mutate()} data-testid="bindings-file-check">Check</button>{' '}
            <button type="button" className="btn btn-sm btn-primary" onClick={() => save.mutate(needs.map((n) => n.scope))} data-testid="bindings-file-save">{needs.length > 0 ? 'Confirm and save' : 'Save the bindings'}</button>
          </div>
          <ErrorBanner error={check.error} />
        </div>
      )}
    </div>
  )
}
