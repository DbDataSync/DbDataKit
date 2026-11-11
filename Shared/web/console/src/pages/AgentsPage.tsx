import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { CodeEditor, ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { ConsoleShell } from '../ConsoleShell'
import {
  agentsApi, bindingLine, connectCommand, providerTemplate, cost, eventWord, percent, promptingMeaning, promptingName, sessions, statusWord, tokenState, waitingOn,
  type AgentDetail, type Approval, type AuditRow, type ConversationView, type Diagnostic, type MadeToken, type PolicyChange, type ProviderTest, type ProviderView,
} from '../agents'
import { SETTINGS, TRAITS, emptyPolicy, policyFromYaml, policyToYaml, settingLabel, settingMeaning, type PolicyForm, type Setting } from '../policyForm'

type Tab = 'chat' | 'inbox' | 'record' | 'agents' | 'models' | 'policy'
const TABS: { id: Tab; label: string }[] = [
  { id: 'chat', label: 'Chat' }, { id: 'inbox', label: 'Approvals' }, { id: 'record', label: 'Record' }, { id: 'agents', label: 'Agents' }, { id: 'models', label: 'Models' }, { id: 'policy', label: 'Install policy' },
]

/**
 * Agents in the console (plan 27a AG14 to AG17, AG29 to AG32): chat with a back-end agent and answer the steps that wait for you (AG15), the approvals inbox and the record of what agents did, in the console or through an outside client (AG29),
 * the agent's definition with what each tool will ask (AG30), the model's space (AG31) and its skills (AG32), and the install policy with a confirmed, recorded loosening (AG17). The server decides everything shown here; a screen never works out what is allowed.
 */
export function AgentsPage() {
  const [params, setParams] = useSearchParams()
  const tab = (TABS.find((t) => t.id === params.get('tab'))?.id ?? 'chat') as Tab
  const pending = useQuery({ queryKey: ['agents', 'approvals', true], queryFn: () => agentsApi.approvals(true, false), refetchInterval: 3000 })
  return (
    <ConsoleShell crumbs={[{ label: 'Agents', heading: true }]}>
      <div className="pane" data-testid="agents-page">
        <div className="page-head">
          <h1 className="page-title">Agents</h1>
          <span className="page-note">Talk to an agent, answer what it asks, and see what agents did and on whose word.</span>
        </div>
        <div className="subtabbar" data-testid="agents-tabs">
          {TABS.map((t) => (
            <button key={t.id} type="button" className={`subtab ${tab === t.id ? 'active' : ''}`} onClick={() => setParams({ tab: t.id })} data-testid={`agents-tab-${t.id}`}>
              {t.label}{t.id === 'inbox' && (pending.data?.length ?? 0) > 0 ? ` (${pending.data!.length})` : ''}
            </button>
          ))}
        </div>
        {tab === 'chat' && <ChatTab />}
        {tab === 'inbox' && <InboxTab />}
        {tab === 'record' && <RecordTab />}
        {tab === 'agents' && <AgentsTab />}
        {tab === 'models' && <ModelsTab />}
        {tab === 'policy' && <PolicyTab />}
      </div>
    </ConsoleShell>
  )
}

// ---- the approval prompt: what the person is asked, in the command's own words ----

function ApprovalPrompt({ approval, onAnswer, busy }: { approval: Approval; onAnswer: (approve: boolean) => void; busy: boolean }) {
  return (
    <div className="banner" role="group" aria-label="An agent asks" data-testid="approval-prompt" data-approval={approval.id}>
      <div>
        <strong>{approval.agent}</strong> <span className="hint">(version {approval.agentVersion}) asks to run</span>
        <div className="mono" data-testid="approval-preview">{approval.preview}</div>
        <div className="hint">What it does: {approval.traits}. Only your answer counts; nothing the agent or a tool says can stand for it.</div>
        <button type="button" className="btn btn-sm btn-primary" disabled={busy} onClick={() => onAnswer(true)} data-testid="approval-approve">Approve</button>{' '}
        <button type="button" className="btn btn-sm" disabled={busy} onClick={() => onAnswer(false)} data-testid="approval-deny">Do not approve</button>
      </div>
    </div>
  )
}

// ---- AG15: chat ----

function ChatTab() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const [agent, setAgent] = useState('')
  const [selected, setSelected] = useState<string | null>(null)
  const [message, setMessage] = useState('')
  const [openTurn, setOpenTurn] = useState<number | null>(null)
  const agents = useQuery({ queryKey: ['agents', 'definitions'], queryFn: agentsApi.definitions })
  const list = useQuery({ queryKey: ['agents', 'conversations'], queryFn: () => agentsApi.conversations(false) })
  const view = useQuery({
    queryKey: ['agents', 'conversation', selected], queryFn: () => agentsApi.conversation(selected!), enabled: selected !== null,
    refetchInterval: (q) => (q.state.data?.running || waitingOn(q.state.data?.approvals ?? []) ? 700 : false),
  })
  const start = useMutation({ mutationFn: () => agentsApi.start(agent), onSuccess: async (c) => { setSelected(c.id); await client.invalidateQueries({ queryKey: ['agents', 'conversations'] }) } })
  const send = useMutation({
    mutationFn: () => agentsApi.send(selected!, message),
    onSuccess: async () => { setMessage(''); await client.invalidateQueries({ queryKey: ['agents', 'conversation', selected] }) },
  })
  const answer = useMutation({
    mutationFn: ({ id, approve }: { id: string; approve: boolean }) => agentsApi.answer(id, approve),
    onSuccess: () => client.invalidateQueries({ queryKey: ['agents'] }),
  })
  const data: ConversationView | undefined = view.data
  const waiting = data ? waitingOn(data.approvals) : null

  return (
    <div className="row" style={{ gap: 24, alignItems: 'flex-start', flexWrap: 'wrap' }} data-testid="agents-chat">
      <div className="card" style={{ minWidth: 260, flex: '0 1 300px' }}>
        <div className="card-body">
          <strong>Conversations</strong>
          {isAdmin ? (
            <div style={{ margin: '8px 0' }}>
              <select value={agent} onChange={(e) => setAgent(e.target.value)} data-testid="chat-agent-select" aria-label="Agent">
                <option value="">Choose an agent…</option>
                {agents.data?.map((a) => <option key={a.name} value={a.name}>{a.title ?? a.name}{a.modelBound ? '' : ' (no model bound)'}</option>)}
              </select>{' '}
              <button type="button" className="btn btn-sm btn-primary" disabled={!agent || start.isPending} onClick={() => start.mutate()} data-testid="chat-start">Start</button>
            </div>
          ) : <div className="hint">Only an administrator talks to an agent here: its tools hold the console's own authority.</div>}
          <ErrorBanner error={start.error ?? agents.error} />
          {list.data?.length === 0 && <div className="hint" data-testid="chat-none">No conversation yet.</div>}
          {list.data?.map((c) => (
            <div key={c.id}>
              <button type="button" className={`btn btn-sm${selected === c.id ? ' btn-primary' : ''}`} style={{ margin: '2px 0', textAlign: 'left' }} onClick={() => setSelected(c.id)} data-testid={`chat-conversation-${c.id}`}>
                {c.title ?? c.agent} · {statusWord(c.status)}
              </button>
            </div>
          ))}
        </div>
      </div>
      <div className="card" style={{ flex: '1 1 480px' }}>
        <div className="card-body">
          {!data && <div className="hint" data-testid="chat-empty">Choose or start a conversation.</div>}
          {data && (
            <div data-testid="chat-conversation" data-status={data.conversation.status}>
              <div className="hint">
                {data.conversation.agent} · version {data.conversation.agentVersion} · {statusWord(data.conversation.status)}{data.running ? ' · answering…' : ''}
                {data.conversation.stopReason ? ` · ${data.conversation.stopReason}` : ''}
              </div>
              <div data-testid="chat-transcript" style={{ margin: '12px 0' }}>
                {data.text.map((t) => (
                  <div key={t.seq} data-role={t.role} style={{ margin: '6px 0' }}><strong>{t.role === 'user' ? 'You' : data.conversation.agent}:</strong> <span style={{ whiteSpace: 'pre-wrap' }}>{t.content}</span></div>
                ))}
                {data.text.length === 0 && <div className="hint">Nothing is kept of what was said (the words are kept for 30 days; the record of what was done stays).</div>}
              </div>
              {waiting && <ApprovalPrompt approval={waiting} busy={answer.isPending} onAnswer={(approve) => answer.mutate({ id: waiting.id, approve })} />}
              <ErrorBanner error={answer.error ?? send.error} />
              {isAdmin && (
                <form onSubmit={(e) => { e.preventDefault(); if (message.trim() && !data.running) send.mutate() }} data-testid="chat-form">
                  <textarea value={message} onChange={(e) => setMessage(e.target.value)} rows={3} style={{ width: '100%' }} placeholder="Ask the agent…" data-testid="chat-input" aria-label="Message" />
                  <button type="submit" className="btn btn-sm btn-primary" disabled={!message.trim() || data.running || send.isPending} data-testid="chat-send">Send</button>
                </form>
              )}
              <details style={{ marginTop: 12 }} data-testid="chat-usage">
                <summary>What it used</summary>
                <table className="table">
                  <thead><tr><th>Turn</th><th>Model</th><th>In</th><th>Out</th><th>Cost</th><th></th></tr></thead>
                  <tbody>
                    {data.turns.map((t) => (
                      <tr key={t.id} data-testid="chat-turn">
                        <td>{t.turn}</td><td>{t.model ?? ''}</td><td>{t.inputTokens}</td><td>{t.outputTokens}</td><td>{cost(t.cost)}</td>
                        <td><button type="button" className="btn btn-sm" onClick={() => setOpenTurn(openTurn === t.turn ? null : t.turn)} data-testid={`chat-turn-context-${t.turn}`}>What it was given</button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {openTurn !== null && (
                  <div data-testid="chat-context">
                    {(data.turns.find((t) => t.turn === openTurn)?.contextRecord ?? 'Not recorded.').split(', ').map((part) => <div key={part} className="mono">{part}</div>)}
                  </div>
                )}
              </details>
              <details style={{ marginTop: 8 }} data-testid="chat-transcript-audit">
                <summary>What it did ({data.audit.length})</summary>
                {data.audit.map((a) => <AuditLine key={a.id} row={a} />)}
              </details>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

function AuditLine({ row }: { row: AuditRow }) {
  return (
    <div data-testid="audit-line" data-kind={row.kind} style={{ margin: '2px 0' }}>
      <span className="hint">{row.occurredAt.replace('T', ' ').slice(0, 19)}</span> <strong>{row.personName ?? row.person}</strong> · {row.agent} <span className="hint">v{row.agentVersion}</span> {eventWord(row.kind)}
      {row.tool ? <> <span className="mono">{row.tool}</span></> : null}{row.detail ? <span className="hint"> — {row.detail}</span> : null}
    </div>
  )
}

// ---- AG29: the inbox ----

function InboxTab() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const [all, setAll] = useState(false)
  const pending = useQuery({ queryKey: ['agents', 'approvals', 'inbox', all], queryFn: () => agentsApi.approvals(true, all), refetchInterval: 3000 })
  const history = useQuery({ queryKey: ['agents', 'approvals', 'history', all], queryFn: () => agentsApi.approvals(false, all) })
  const answer = useMutation({ mutationFn: ({ id, approve }: { id: string; approve: boolean }) => agentsApi.answer(id, approve), onSuccess: () => client.invalidateQueries({ queryKey: ['agents'] }) })
  return (
    <div data-testid="agents-inbox">
      <p className="hint">Steps agents ask a person about wait here: the ones in a conversation in this console, and the ones an outside agent (a client using <span className="mono">ui mcp</span>) could not ask its own person about. Only the person a step is for can answer it.</p>
      {isAdmin && <label><input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} data-testid="inbox-all" /> Show everyone's</label>}
      <ErrorBanner error={pending.error ?? answer.error} />
      {pending.data?.length === 0 && <div className="empty" data-testid="inbox-empty">Nothing is waiting for you.</div>}
      {pending.data?.map((a) => (
        <div key={a.id} style={{ margin: '8px 0' }}>
          <div className="hint">{a.origin === 'mcp' ? 'An outside agent' : 'In the console'} · {a.personName ?? a.person} · asked {a.requestedAt.replace('T', ' ').slice(0, 19)}</div>
          <ApprovalPrompt approval={a} busy={answer.isPending} onAnswer={(approve) => answer.mutate({ id: a.id, approve })} />
        </div>
      ))}
      <h3>Answered</h3>
      <table className="table" data-testid="inbox-history">
        <thead><tr><th>Step</th><th>Agent</th><th>For</th><th>Answer</th><th>By</th></tr></thead>
        <tbody>
          {history.data?.filter((a) => a.status !== 'pending').map((a) => (
            <tr key={a.id} data-status={a.status}><td className="mono">{a.preview}</td><td>{a.agent} <span className="hint">v{a.agentVersion}</span></td><td>{a.personName ?? a.person}</td><td>{a.status}</td><td>{a.answeredByName ?? a.answeredBy ?? ''}{a.note ? ` (${a.note})` : ''}</td></tr>
          ))}
        </tbody>
      </table>
      <OutsideAgents />
    </div>
  )
}

/** Connect an outside agent (`ui mcp --report-to`): a token of the person's own, shown once, and the lines to put in the agent's configuration. */
function OutsideAgents() {
  const client = useQueryClient()
  const [name, setName] = useState('')
  const [made, setMade] = useState<MadeToken | null>(null)
  const tokens = useQuery({ queryKey: ['agents', 'tokens'], queryFn: agentsApi.tokens })
  const make = useMutation({ mutationFn: () => agentsApi.makeToken(name.trim()), onSuccess: (m) => { setMade(m); setName(''); client.invalidateQueries({ queryKey: ['agents', 'tokens'] }) } })
  const revoke = useMutation({ mutationFn: (id: string) => agentsApi.revokeToken(id), onSuccess: () => client.invalidateQueries({ queryKey: ['agents', 'tokens'] }) })
  const connect = connectCommand('dbdatakit', window.location.origin, made?.user ?? '<your account id>', made?.token ?? null)
  return (
    <section data-testid="outside-agents" style={{ marginTop: 24 }}>
      <h3>Connect an outside agent</h3>
      <p className="hint">An agent in another tool (Claude Code, an IDE) that uses <span className="mono">ui mcp</span> can have its session recorded here and, when its client cannot ask you, ask you in this inbox. Make a token, give it to the agent&apos;s configuration, and revoke it here when it is no longer wanted. The token is yours: it acts as you, and only for this: it reaches nothing else of the console.</p>
      <ErrorBanner error={tokens.error ?? make.error ?? revoke.error} />
      <form onSubmit={(e) => { e.preventDefault(); if (name.trim()) make.mutate() }} style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        <input value={name} onChange={(e) => setName(e.target.value)} placeholder="A name, such as my laptop" maxLength={100} data-testid="token-name" />
        <button type="submit" className="btn btn-sm btn-primary" disabled={!name.trim() || make.isPending} data-testid="token-make">Make a token</button>
      </form>
      {made && (
        <div className="banner" data-testid="token-made" style={{ margin: '8px 0' }}>
          <div><strong>The token is shown once.</strong> Copy it now; the console keeps only a fingerprint.</div>
          <pre className="mono" data-testid="token-secret">{made.token}</pre>
        </div>
      )}
      <div className="hint" style={{ marginTop: 8 }}>In the agent&apos;s MCP configuration:</div>
      <pre className="mono" data-testid="token-command">{connect.command}{'\n'}{connect.environment.map((e) => `${e.name}=${e.value}`).join('\n')}</pre>
      <table className="table" data-testid="token-list">
        <thead><tr><th>Name</th><th>Made</th><th>Expires</th><th>Last used</th><th>State</th><th /></tr></thead>
        <tbody>
          {tokens.data?.map((t) => (
            <tr key={t.id} data-state={tokenState(t)}>
              <td>{t.name} <span className="hint mono">{t.id}</span></td><td>{t.createdAt.slice(0, 10)}</td><td>{t.expiresAt?.slice(0, 10) ?? ''}</td><td>{t.lastUsedAt?.replace('T', ' ').slice(0, 16) ?? 'never'}</td><td>{tokenState(t)}</td>
              <td>{t.active && <button type="button" className="btn btn-sm" onClick={() => revoke.mutate(t.id)} data-testid={`token-revoke-${t.id}`}>Revoke</button>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}

// ---- AG05: model providers, the key, Test and the slots ----

const KINDS = ['anthropic', 'openai', 'foundry', 'ollama', 'openai-compatible']

function ModelsTab() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const models = useQuery({ queryKey: ['agents', 'models'], queryFn: agentsApi.models })
  const [selected, setSelected] = useState<string | null>(null)
  const [yaml, setYaml] = useState('')
  const [problems, setProblems] = useState<Diagnostic[]>([])
  const [newName, setNewName] = useState('')
  const [newKind, setNewKind] = useState('anthropic')
  const [key, setKey] = useState('')
  const [tests, setTests] = useState<Record<string, ProviderTest>>({})
  const [slotsYaml, setSlotsYaml] = useState<string | null>(null)
  const [slotProblems, setSlotProblems] = useState<Diagnostic[]>([])
  const refresh = () => client.invalidateQueries({ queryKey: ['agents'] })
  const provider: ProviderView | undefined = models.data?.providers.find((p) => p.name === selected)
  useEffect(() => { if (models.data && slotsYaml === null) setSlotsYaml(models.data.slotsYaml || 'model-slots: 1\nslots:\n  default:\n    "*":\n      - { provider: <provider>, model: <model> }\n') }, [models.data, slotsYaml])
  useEffect(() => { if (provider) { setYaml(provider.yaml); setProblems(provider.diagnostics) } }, [provider?.name, provider?.yaml]) // eslint-disable-line react-hooks/exhaustive-deps

  const save = useMutation({ mutationFn: () => agentsApi.saveProvider(selected!, yaml), onSuccess: (r) => { setProblems(r.diagnostics); refresh() } })
  const check = useMutation({ mutationFn: () => agentsApi.checkProvider(selected!, yaml), onSuccess: (r) => setProblems(r.diagnostics) })
  const remove = useMutation({ mutationFn: () => agentsApi.deleteProvider(selected!), onSuccess: () => { setSelected(null); refresh() } })
  const setTheKey = useMutation({ mutationFn: () => agentsApi.setKey(selected!, key), onSuccess: () => { setKey(''); refresh() } })
  const test = useMutation({ mutationFn: (model: string) => agentsApi.testProvider(selected!, model).then((r) => [model, r] as const), onSuccess: ([model, r]) => setTests((t) => ({ ...t, [`${selected}/${model}`]: r })) })
  const saveSlots = useMutation({ mutationFn: () => agentsApi.saveSlots(slotsYaml ?? ''), onSuccess: (r) => { setSlotProblems(r.diagnostics); refresh() } })
  const checkSlots = useMutation({ mutationFn: () => agentsApi.checkSlots(slotsYaml ?? ''), onSuccess: (r) => setSlotProblems(r.diagnostics) })
  // a refused save carries the reader's diagnostics in its body: shown as the check shows them
  const refused = (e: unknown): Diagnostic[] | null => (e as { body?: { diagnostics?: Diagnostic[] } } | null)?.body?.diagnostics ?? null
  const shownProblems = problems.length > 0 ? problems : refused(save.error) ?? []
  const shownSlotProblems = slotProblems.length > 0 ? slotProblems : refused(saveSlots.error) ?? []
  const severityOf = (d: Diagnostic) => (d.severity === 'error' ? 'error' as const : d.severity === 'warning' ? 'warning' as const : 'info' as const)

  const start = () => { if (!/^[a-z][a-z0-9-]{0,63}$/.test(newName)) return; setSelected(newName); setYaml(providerTemplate(newKind, newName)); setProblems([]); setNewName('') }

  return (
    <div data-testid="agents-models">
      <p className="hint">Where an agent&apos;s model comes from. A provider is a file (<span className="mono">models/&lt;name&gt;.yml</span>): its kind, address, the models it allows with their prices, and the <em>reference</em> of its key. The key you type here is kept in the console&apos;s secret store, never in the file. A slot (the name an agent asks for) is bound to an ordered list of models: the first is used and the others take over when it is down.</p>
      <ErrorBanner error={models.error ?? check.error ?? remove.error ?? setTheKey.error ?? test.error ?? checkSlots.error} />
      {models.data?.agentSlots.map((s) => (
        <div key={s.slot} data-testid={`slot-state-${s.slot}`} data-bound={s.bound} className={s.bound ? 'hint' : 'banner'}>
          Slot <span className="mono">{s.slot}</span> (asked for by {s.agents.join(', ')}): {s.bound ? 'bound to a model that can be used.' : 'nothing usable is bound. Add a provider and bind the slot below; an agent on it cannot start a conversation until then.'}
        </div>
      ))}
      <div style={{ display: 'flex', gap: 24, alignItems: 'flex-start', flexWrap: 'wrap' }}>
        <div style={{ minWidth: 220 }}>
          <strong>Providers</strong>
          {models.data?.providers.length === 0 && <div className="empty" data-testid="providers-empty">No provider yet.</div>}
          {models.data?.providers.map((p) => (
            <div key={p.name}><button type="button" className={`btn btn-sm${selected === p.name ? ' btn-primary' : ''}`} style={{ margin: '2px 0' }} onClick={() => setSelected(p.name)} data-testid={`provider-${p.name}`}>{p.name}</button>{' '}
              <span className="hint">{p.kind ?? 'not readable'}{p.cloud === null ? '' : p.cloud ? ' · cloud' : ' · local'}{p.diagnostics.length > 0 ? ' · has problems' : p.keyAvailable ? '' : ' · key not set'}</span></div>
          ))}
          {isAdmin && (
            <form onSubmit={(e) => { e.preventDefault(); start() }} style={{ marginTop: 12 }}>
              <input value={newName} onChange={(e) => setNewName(e.target.value)} placeholder="a name, such as claude" data-testid="provider-new-name" />{' '}
              <select value={newKind} onChange={(e) => setNewKind(e.target.value)} data-testid="provider-new-kind">{KINDS.map((k) => <option key={k}>{k}</option>)}</select>{' '}
              <button type="submit" className="btn btn-sm" disabled={!newName} data-testid="provider-new">Add a provider</button>
            </form>
          )}
        </div>
        {selected && (
          <div style={{ flex: 1, minWidth: 360 }} data-testid="provider-editor">
            <CodeEditor value={yaml} language="yaml" onChange={(v) => { setYaml(v); setProblems([]) }} diagnostics={shownProblems.map((d) => ({ line: d.line, column: d.column, message: d.message, severity: severityOf(d) }))} readOnly={!isAdmin} testId="provider-yaml" />
            <div data-testid="provider-diagnostics">{shownProblems.map((d, i) => <div key={i} data-testid="provider-diagnostic" data-severity={d.severity}><span className="mono">{d.code}</span> line {d.line}: {d.message}{d.fix ? ` ${d.fix}` : ''}</div>)}</div>
            {isAdmin && <div style={{ margin: '8px 0' }}>
              <button type="button" className="btn btn-sm" onClick={() => check.mutate()} data-testid="provider-check">Check</button>{' '}
              <button type="button" className="btn btn-sm btn-primary" onClick={() => save.mutate()} disabled={save.isPending} data-testid="provider-save">Save</button>{' '}
              {provider && <button type="button" className="btn btn-sm" onClick={() => remove.mutate()} data-testid="provider-remove">Remove</button>}
            </div>}
            {provider && (
              <>
                <div data-testid="provider-key" data-available={provider.keyAvailable}>
                  <strong>Key</strong> <span className="mono">{provider.keyReference ?? '(none)'}</span>: {provider.keyAvailable ? 'it can be read.' : provider.keyProblem ?? 'not set.'}
                  {isAdmin && provider.keyReference === `secret:dbdata:console:model-provider:${provider.name}:key` && (
                    <form onSubmit={(e) => { e.preventDefault(); if (key) setTheKey.mutate() }} style={{ marginTop: 4 }}>
                      <input type="password" autoComplete="off" value={key} onChange={(e) => setKey(e.target.value)} placeholder="paste the key; it is kept in the secret store and not shown again" style={{ minWidth: 320 }} data-testid="provider-key-input" />{' '}
                      <button type="submit" className="btn btn-sm" disabled={!key} data-testid="provider-key-save">Keep the key</button>
                    </form>
                  )}
                </div>
                <table className="table" data-testid="provider-models">
                  <thead><tr><th>Model</th><th>Window</th><th>Price per million (in / out)</th><th>Test</th><th /></tr></thead>
                  <tbody>
                    {provider.models.map((m) => {
                      const t = tests[`${provider.name}/${m.id}`]
                      return (
                        <tr key={m.id}>
                          <td className="mono">{m.id}</td><td>{m.window.toLocaleString()}</td><td>{m.priceInput === null ? 'not priced' : `${m.priceInput} / ${m.priceOutput}`}</td>
                          <td>{isAdmin && <button type="button" className="btn btn-sm" disabled={test.isPending} onClick={() => test.mutate(m.id)} data-testid={`model-test-${m.id}`}>Test</button>}</td>
                          <td>{t && <span data-testid={`model-test-result-${m.id}`} data-ok={t.ok}>{t.ok ? `${t.message} It said: ${t.answer} (${t.milliseconds} ms)` : t.message}</span>}</td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </>
            )}
          </div>
        )}
      </div>
      <h3>Slots</h3>
      <p className="hint">Per slot and channel (<span className="mono">&quot;*&quot;</span> for every channel without its own), an ordered list: the primary first, then the fallback.</p>
      <table className="table" data-testid="slot-bindings">
        <thead><tr><th>Slot</th><th>Channel</th><th>Bound to</th></tr></thead>
        <tbody>{models.data?.bindings.map((b) => <tr key={`${b.slot}/${b.channel}`}><td className="mono">{b.slot}</td><td className="mono">{b.channel}</td><td>{bindingLine(b)}</td></tr>)}</tbody>
      </table>
      {slotsYaml !== null && (
        <>
          <CodeEditor value={slotsYaml} language="yaml" onChange={(v) => { setSlotsYaml(v); setSlotProblems([]) }} diagnostics={shownSlotProblems.map((d) => ({ line: d.line, column: d.column, message: d.message, severity: severityOf(d) }))} readOnly={!isAdmin} testId="slots-yaml" />
          <div data-testid="slot-diagnostics">{shownSlotProblems.map((d, i) => <div key={i} data-testid="slot-diagnostic" data-severity={d.severity}><span className="mono">{d.code}</span> line {d.line}: {d.message}{d.fix ? ` ${d.fix}` : ''}</div>)}</div>
          {isAdmin && <div style={{ margin: '8px 0' }}><button type="button" className="btn btn-sm" onClick={() => checkSlots.mutate()} data-testid="slots-check">Check</button>{' '}<button type="button" className="btn btn-sm btn-primary" onClick={() => saveSlots.mutate()} data-testid="slots-save">Save the bindings</button></div>}
        </>
      )}
    </div>
  )
}

// ---- AG29: the record, with usage ----

function RecordTab() {
  const isAdmin = useIsAdmin()
  const [person, setPerson] = useState('')
  const [agent, setAgent] = useState('')
  const [origin, setOrigin] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  const audit = useQuery({ queryKey: ['agents', 'audit', person, agent, origin], queryFn: () => agentsApi.audit({ person, agent, origin }) })
  const usage = useQuery({ queryKey: ['agents', 'usage'], queryFn: agentsApi.usage, enabled: isAdmin })
  const grouped = useMemo(() => sessions(audit.data ?? []), [audit.data])
  return (
    <div data-testid="agents-record">
      <div className="row" style={{ gap: 12, flexWrap: 'wrap', margin: '8px 0' }}>
        {isAdmin && <input placeholder="Person" value={person} onChange={(e) => setPerson(e.target.value)} data-testid="record-person" aria-label="Person" />}
        <input placeholder="Agent" value={agent} onChange={(e) => setAgent(e.target.value)} data-testid="record-agent" aria-label="Agent" />
        <select value={origin} onChange={(e) => setOrigin(e.target.value)} data-testid="record-origin" aria-label="Where from">
          <option value="">Everywhere</option><option value="console">The console</option><option value="mcp">An outside agent (MCP)</option>
        </select>
      </div>
      <ErrorBanner error={audit.error} />
      {grouped.length === 0 && <div className="empty" data-testid="record-empty">Nothing recorded for this filter.</div>}
      {grouped.map((s) => (
        <div key={s.key} className="card" style={{ marginBottom: 8 }} data-testid="record-session" data-origin={s.origin}>
          <div className="card-body">
            <button type="button" className="btn btn-sm" onClick={() => setOpen(open === s.key ? null : s.key)} data-testid="record-session-toggle">
              {s.agent} <span className="hint">v{s.agentVersion}</span> for {s.person} · {s.origin === 'mcp' ? 'outside agent' : 'console'} · {s.rows.length} step{s.rows.length === 1 ? '' : 's'} · last {s.last.replace('T', ' ').slice(0, 19)}
            </button>
            {open === s.key && <div data-testid="record-session-rows">{[...s.rows].reverse().map((r) => <AuditLine key={r.id} row={r} />)}</div>}
          </div>
        </div>
      ))}
      {isAdmin && usage.data && (
        <>
          <h3>Used</h3>
          <table className="table" data-testid="record-usage">
            <thead><tr><th></th><th>Turns</th><th>Tokens in</th><th>Tokens out</th><th>Cost</th></tr></thead>
            <tbody>
              {usage.data.install && <tr data-testid="usage-install"><td><strong>The install</strong></td><td>{usage.data.install.turns}</td><td>{usage.data.install.inputTokens}</td><td>{usage.data.install.outputTokens}</td><td>{cost(usage.data.install.cost)}</td></tr>}
              {usage.data.byAgent.map((u) => <tr key={`a-${u.key}`}><td>agent {u.key}</td><td>{u.turns}</td><td>{u.inputTokens}</td><td>{u.outputTokens}</td><td>{cost(u.cost)}</td></tr>)}
              {usage.data.byPerson.map((u) => <tr key={`p-${u.key}`}><td>person {u.personName ?? u.key}</td><td>{u.turns}</td><td>{u.inputTokens}</td><td>{u.outputTokens}</td><td>{cost(u.cost)}</td></tr>)}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}

// ---- AG16 (in the console's folder), AG30, AG31, AG32: an agent ----

const severity = (d: Diagnostic) => (d.severity === 'error' ? 'error' as const : d.severity === 'warning' ? 'warning' as const : 'info' as const)

function AgentsTab() {
  const [params, setParams] = useSearchParams()
  const name = params.get('agent')
  const agents = useQuery({ queryKey: ['agents', 'definitions'], queryFn: agentsApi.definitions })
  return (
    <div className="row" style={{ gap: 24, alignItems: 'flex-start', flexWrap: 'wrap' }} data-testid="agents-definitions">
      <div className="card" style={{ minWidth: 240, flex: '0 1 280px' }}>
        <div className="card-body">
          <strong>Agents</strong>
          <ErrorBanner error={agents.error} />
          {agents.data?.length === 0 && <div className="hint" data-testid="agents-none">No agent is defined. An agent is a folder <span className="mono">agents/&lt;name&gt;/agent.yml</span> in the console's directory.</div>}
          {agents.data?.map((a) => (
            <div key={a.name}>
              <button type="button" className={`btn btn-sm${name === a.name ? ' btn-primary' : ''}`} style={{ margin: '2px 0', textAlign: 'left' }} onClick={() => setParams({ tab: 'agents', agent: a.name })} data-testid={`agent-${a.name}`}>
                {a.title ?? a.name}{a.problems > 0 ? ` · ${a.problems} problem${a.problems === 1 ? '' : 's'}` : ''}
              </button>
            </div>
          ))}
        </div>
      </div>
      <div style={{ flex: '1 1 520px', minWidth: 0 }}>{name ? <AgentEditor key={name} name={name} /> : <div className="hint">Choose an agent.</div>}</div>
    </div>
  )
}

function AgentEditor({ name }: { name: string }) {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const detail = useQuery({ queryKey: ['agents', 'definition', name], queryFn: () => agentsApi.definition(name) })
  const [yaml, setYaml] = useState<string | null>(null)
  const [checked, setChecked] = useState<AgentDetail | null>(null)
  const [saved, setSaved] = useState(false)
  useEffect(() => { if (detail.data && yaml === null) setYaml(detail.data.yaml) }, [detail.data, yaml])
  // `agent check` as you type: the server reads the text and says what is wrong and where
  useEffect(() => {
    if (yaml === null || yaml === detail.data?.yaml) { setChecked(null); return }
    const handle = setTimeout(() => { agentsApi.check(name, yaml).then(setChecked).catch(() => setChecked(null)) }, 400)
    return () => clearTimeout(handle)
  }, [yaml, name, detail.data?.yaml])
  const save = useMutation({
    mutationFn: () => agentsApi.save(name, yaml ?? ''),
    onSuccess: async () => { setSaved(true); setChecked(null); await client.invalidateQueries({ queryKey: ['agents'] }) },
    onError: () => setSaved(false),
  })
  const shown = checked ?? detail.data
  if (!shown || yaml === null) return <ErrorBanner error={detail.error} />
  const problems = shown.diagnostics
  const errors = problems.filter((d) => d.severity === 'error')
  return (
    <div data-testid="agent-editor" data-agent={name}>
      <h2>{name}</h2>
      <ErrorBanner error={detail.error ?? save.error} />
      <div className="card"><div className="card-body">
        <strong>Definition</strong> <span className="hint mono">{shown.file}</span>
        <CodeEditor value={yaml} language="yaml" onChange={(v) => { setYaml(v); setSaved(false) }} diagnostics={problems.map((d) => ({ line: d.line, column: d.column, message: d.message, severity: severity(d) }))} readOnly={!isAdmin} testId="agent-yaml" />
        <div data-testid="agent-diagnostics">
          {problems.length === 0 && <div className="hint" data-testid="agent-check-ok">agent check: nothing to report.</div>}
          {problems.map((d, i) => <div key={i} data-testid="agent-diagnostic" data-severity={d.severity}><span className="mono">{d.code}</span> line {d.line}: {d.message}{d.fix ? ` ${d.fix}` : ''}</div>)}
        </div>
        {isAdmin && <button type="button" className="btn btn-sm btn-primary" disabled={errors.length > 0 || save.isPending || yaml === detail.data?.yaml} onClick={() => save.mutate()} data-testid="agent-save">Save</button>}
        {saved && <span className="hint" data-testid="agent-saved"> Saved.</span>}
      </div></div>

      <div className="card" style={{ marginTop: 12 }}><div className="card-body" data-testid="agent-prompting">
        <strong>What each tool will ask</strong>
        <p className="hint">From the same rule the run uses: the strictest of the tool's own default, the install policy and this agent's setting.</p>
        <table className="table">
          <thead><tr><th>Tool</th><th>Does</th><th>Asks</th><th>Set by</th></tr></thead>
          <tbody>
            {shown.prompting.map((p) => (
              <tr key={p.tool} data-testid="prompting-row" data-tool={p.tool} data-effective={promptingName(p.effective)}>
                <td className="mono">{p.tool}</td><td>{p.traits.join(', ')}</td>
                <td><strong>{promptingName(p.effective)}</strong><div className="hint">{promptingMeaning(p.effective)}</div>{p.warning && <div className="banner error" data-testid="prompting-warning">{p.warning}</div>}</td>
                <td>{p.layer}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {shown.prompting.length === 0 && <div className="hint">The agent names no tool the console offers.</div>}
      </div></div>

      {shown.space && (
        <div className="card" style={{ marginTop: 12 }}><div className="card-body" data-testid="agent-space" data-over={shown.space.over}>
          <strong>The model's space</strong>
          <p className="hint">What is always given before the conversation starts, against {percent(shown.space.budgetTokens / shown.space.window)} of the model's {shown.space.window}-token window; counted with {shown.space.basis}.</p>
          <div data-testid="space-total">{shown.space.totalTokens} of {shown.space.budgetTokens} tokens ({percent(shown.space.usedShareOfBudget)} of the budget)</div>
          <div style={{ background: 'var(--border, #ddd)', height: 10, borderRadius: 4, margin: '6px 0' }} aria-hidden>
            <div style={{ width: `${Math.min(100, shown.space.usedShareOfBudget * 100)}%`, height: 10, borderRadius: 4, background: shown.space.over ? 'var(--danger, #c0392b)' : 'var(--accent, #2d6cdf)' }} />
          </div>
          <table className="table"><tbody>
            {shown.space.groups.map((g) => <tr key={g.name} data-testid="space-group"><td>{g.name}</td><td>{g.tokens} tokens</td><td>{percent(g.share)} of the window</td></tr>)}
          </tbody></table>
          {shown.space.refusal && <div className="banner error" data-testid="space-refusal">{shown.space.refusal} A publish would be refused.</div>}
          {!shown.space.over && <div className="hint" data-testid="space-ok">Within the budget: a publish is allowed.</div>}
        </div></div>
      )}

      <div className="card" style={{ marginTop: 12 }}><div className="card-body" data-testid="agent-skills">
        <strong>Skills</strong>
        {shown.skills.length === 0 && <div className="hint">The agent is offered no skill.</div>}
        {shown.skills.map((s) => (
          <div key={s.name} data-testid="skill" data-skill={s.name} data-fits={s.fits} style={{ margin: '8px 0' }}>
            <div><span className="mono">{s.name}</span> <span className="hint">version {s.version}</span></div>
            <div>The model sees: <em>{s.description}</em> <span className="hint">({s.descriptionTokens} tokens every turn)</span></div>
            <div className="hint">Body: {s.bodyTokens} tokens when loaded.</div>
            {!s.fits && s.refusal && <div className="banner error" data-testid="skill-refusal">{s.refusal}</div>}
            <ExportButtons files={s.exportFiles} />
          </div>
        ))}
      </div></div>
    </div>
  )
}

function ExportButtons({ files }: { files: Record<string, string> }) {
  const download = (path: string, text: string) => {
    const url = URL.createObjectURL(new Blob([text], { type: 'text/markdown' }))
    const a = document.createElement('a')
    a.href = url
    a.download = path.split('/').pop() ?? 'SKILL.md'
    a.click()
    URL.revokeObjectURL(url)
  }
  return <>{Object.entries(files).map(([path, text]) => <button key={path} type="button" className="btn btn-sm" onClick={() => download(path, text)} data-testid="skill-export">Export as <span className="mono">{path}</span></button>)}</>
}

// ---- AG17: the install policy ----

function PolicyTab() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const current = useQuery({ queryKey: ['agents', 'policy'], queryFn: agentsApi.policy })
  const [form, setForm] = useState<PolicyForm | null>(null)
  const [changes, setChanges] = useState<PolicyChange[] | null>(null)
  const [problems, setProblems] = useState<Diagnostic[]>([])
  const [confirming, setConfirming] = useState(false)
  const [saved, setSaved] = useState(false)
  useEffect(() => { if (current.data && form === null) setForm(policyFromYaml(current.data.yaml).form ?? emptyPolicy()) }, [current.data, form])
  const yaml = form ? policyToYaml(form) : ''
  const preview = useMutation({ mutationFn: () => agentsApi.previewPolicy(yaml), onSuccess: (p) => { setChanges(p.changes); setProblems(p.diagnostics); setSaved(false); setConfirming(p.loosens) } })
  const save = useMutation({
    mutationFn: (confirmed: boolean) => agentsApi.savePolicy(yaml, confirmed),
    onSuccess: async () => { setSaved(true); setConfirming(false); setChanges(null); await client.invalidateQueries({ queryKey: ['agents'] }) },
  })
  if (!form) return <ErrorBanner error={current.error} />
  const set = (patch: Partial<PolicyForm>) => { setForm({ ...form, ...patch }); setChanges(null); setSaved(false); setConfirming(false) }
  const limit = (scope: 'conversation' | 'person' | 'install', key: 'turns' | 'toolCalls' | 'tokens' | 'cost', label: string) => (
    <label key={key} style={{ marginRight: 12 }}>{label} <input type="number" min={1} value={form[scope][key]} disabled={!isAdmin} onChange={(e) => set({ [scope]: { ...form[scope], [key]: e.target.value } } as Partial<PolicyForm>)} data-testid={`policy-limit-${scope}-${key}`} style={{ width: 90 }} /></label>
  )
  const loosening = changes?.filter((c) => c.loosens) ?? []

  return (
    <div data-testid="agents-policy">
      <p className="hint">What no agent can go below. The strictest of the tool's own default, this policy and the agent's own setting applies. Policy <em>text</em> (what agents are told) is a guardrail in the install knowledge project, not here.</p>
      <ErrorBanner error={current.error ?? preview.error ?? save.error} />
      <div className="card" style={{ marginBottom: 12 }}><div className="card-body">
        <strong>Prompting minimums</strong>
        {TRAITS.map((t) => (
          <div key={t.name} style={{ margin: '8px 0' }} data-testid={`policy-trait-${t.name}`}>
            <span className="mono" style={{ display: 'inline-block', minWidth: 170 }}>{t.name}</span>
            <select value={form.minimums[t.name] ?? ''} disabled={!isAdmin} onChange={(e) => set({ minimums: { ...form.minimums, [t.name]: e.target.value as Setting } })} data-testid={`policy-minimum-${t.name}`} aria-label={`Minimum for ${t.name}`}>
              {SETTINGS.map((s) => <option key={s} value={s}>{settingLabel(s)}</option>)}
            </select>
            <div className="hint">{settingMeaning(t.name, (form.minimums[t.name] ?? '') as Setting)}</div>
          </div>
        ))}
      </div></div>
      <div className="card" style={{ marginBottom: 12 }}><div className="card-body">
        <strong>Limits</strong>
        {(['conversation', 'person', 'install'] as const).map((scope) => (
          <div key={scope} style={{ margin: '8px 0' }}>
            <span style={{ display: 'inline-block', minWidth: 110 }}>Per {scope}</span>
            {limit(scope, 'turns', 'model turns')}{limit(scope, 'toolCalls', 'tool calls')}{limit(scope, 'tokens', 'tokens')}{limit(scope, 'cost', 'cost')}
          </div>
        ))}
        <label>Tell administrators at <input type="number" min={1} max={99} value={form.notifyAt} disabled={!isAdmin} onChange={(e) => set({ notifyAt: e.target.value })} data-testid="policy-notify-at" style={{ width: 60 }} />% of a limit</label>
      </div></div>
      <div className="card" style={{ marginBottom: 12 }}><div className="card-body">
        <strong>Agents bound to a cloud provider</strong>
        <div>
          <select value={form.cloudReadsData} disabled={!isAdmin} onChange={(e) => set({ cloudReadsData: e.target.value as PolicyForm['cloudReadsData'] })} data-testid="policy-cloud-reads-data" aria-label="Commands that read data">
            <option value="allow">may run commands that read data</option><option value="ask">ask every time before reading data</option><option value="refuse">may not read data</option>
          </select>
          <div className="hint">Rows and values reach the provider when its agent reads data; a local model keeps them here.</div>
        </div>
      </div></div>
      {isAdmin && <button type="button" className="btn btn-sm btn-primary" disabled={preview.isPending} onClick={() => preview.mutate()} data-testid="policy-review">Review the change</button>}
      {problems.map((d, i) => <div key={i} className="banner error" data-testid="policy-diagnostic">line {d.line}: {d.message}</div>)}
      {changes && (
        <div data-testid="policy-changes" style={{ marginTop: 12 }}>
          {changes.length === 0 && <div className="hint" data-testid="policy-no-change">Nothing changes.</div>}
          {changes.map((c, i) => <div key={i} data-testid="policy-change" data-loosens={c.loosens}>{c.loosens ? 'Less strict: ' : ''}{c.text}</div>)}
          {problems.length === 0 && changes.length > 0 && !confirming && <button type="button" className="btn btn-sm btn-primary" onClick={() => save.mutate(false)} data-testid="policy-save">Save</button>}
          {confirming && loosening.length > 0 && (
            <div className="banner error" role="alertdialog" data-testid="policy-confirm">
              <div>
                <strong>This makes the install less strict.</strong> {loosening.length} change{loosening.length === 1 ? '' : 's'} above lower{loosening.length === 1 ? 's' : ''} a protection. Who confirms it and what changed are recorded.
                <div><button type="button" className="btn btn-sm btn-primary" onClick={() => save.mutate(true)} data-testid="policy-confirm-save">I understand: save it</button></div>
              </div>
            </div>
          )}
        </div>
      )}
      {saved && <div className="hint" data-testid="policy-saved">Saved. The change is in the record.</div>}
    </div>
  )
}
