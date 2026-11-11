import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { CodeEditor, ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { ConsoleShell } from '../ConsoleShell'
import { agentsApi, percent } from '../agents'
import {
  contextLine, isPolicyItem, knowledgeApi, parseInPlay, policyItemId, policyItemText, refreshLine, subjectSource, workActions, workWord,
  type ChunkPreview, type KnowledgeOverview, type Reach, type ScopePreview, type WorkItem,
} from '../knowledge'

type Tab = 'overview' | 'worklist' | 'items' | 'install'
const TABS: { id: Tab; label: string }[] = [
  { id: 'overview', label: 'Index and context' }, { id: 'worklist', label: 'Worklists' }, { id: 'items', label: 'Items and preview' }, { id: 'install', label: 'Install knowledge' },
]

/**
 * The console's knowledge screens (plan 27a KN28, KN36, KN37, KN38): what the files say and what an agent would be given (KN28), the owners' worklists of orphaned items (KN37), an item as it is typed with how it would be split and what
 * would be given for what is in play (KN38, the item editor), and the install knowledge project with the administrators' policy guardrails (KN36). There is no search here: the index and the embedding model are not built, and the screen says so.
 */
export function KnowledgePage() {
  const [params, setParams] = useSearchParams()
  const tab = (TABS.find((t) => t.id === params.get('tab'))?.id ?? 'overview') as Tab
  const overview = useQuery({ queryKey: ['knowledge', 'overview'], queryFn: knowledgeApi.overview })
  return (
    <ConsoleShell crumbs={[{ label: 'Knowledge', heading: true }]}>
      <div className="pane" data-testid="knowledge-page">
        <div className="page-head">
          <h1 className="page-title">Knowledge</h1>
          <span className="page-note">What people have written down for agents and for each other, whose it is, and what an agent would be given.</span>
        </div>
        <div className="subtabbar" data-testid="knowledge-tabs">
          {TABS.map((t) => (
            <button key={t.id} type="button" className={`subtab ${tab === t.id ? 'active' : ''}`} onClick={() => setParams({ tab: t.id })} data-testid={`knowledge-tab-${t.id}`}>
              {t.label}{t.id === 'worklist' && (overview.data?.worklists.reduce((n, l) => n + l.work.length, 0) ?? 0) > 0 ? ` (${overview.data!.worklists.reduce((n, l) => n + l.work.length, 0)})` : ''}
            </button>
          ))}
        </div>
        <ErrorBanner error={overview.error} />
        {overview.data && tab === 'overview' && <OverviewTab overview={overview.data} />}
        {overview.data && tab === 'worklist' && <WorklistTab overview={overview.data} />}
        {overview.data && tab === 'items' && <ItemsTab overview={overview.data} />}
        {overview.data && tab === 'install' && <InstallTab overview={overview.data} />}
      </div>
    </ConsoleShell>
  )
}

// ---- KN28 ----

function OverviewTab({ overview }: { overview: KnowledgeOverview }) {
  const [agent, setAgent] = useState('')
  const [inPlay, setInPlay] = useState('')
  const agents = useQuery({ queryKey: ['agents', 'definitions'], queryFn: agentsApi.definitions })
  const context = useMutation({ mutationFn: () => knowledgeApi.context(agent, parseInPlay(inPlay)) })
  const report = context.data
  return (
    <div data-testid="knowledge-overview">
      <div className="banner" data-testid="knowledge-index-state"><span>{overview.index.message}</span></div>
      <table className="table" data-testid="knowledge-projects">
        <thead><tr><th>Project</th><th>Items</th><th>Chunks</th><th>Instructions</th><th>Guardrails</th><th>References</th><th>Failed their checks</th><th>Orphaned</th></tr></thead>
        <tbody>
          {overview.projects.map((p) => (
            <tr key={p.name} data-testid="knowledge-project" data-project={p.name} data-exists={p.exists}>
              <td>{p.name === 'install' ? 'Install knowledge' : 'Knowledge'}{p.exists ? '' : ' (not made)'}</td><td>{p.items}</td><td>{p.chunks}</td><td>{p.instructions}</td><td>{p.guardrails}</td><td>{p.references}</td>
              <td data-testid="knowledge-failed">{p.failed.length}{p.failed.map((d, i) => <div key={i} className="hint">{d.file} line {d.line}: {d.message}</div>)}</td>
              <td data-testid="knowledge-orphans">{p.orphans.length}{p.orphans.map((o) => <div key={o} className="mono">{o}</div>)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="card" style={{ marginTop: 16 }}><div className="card-body" data-testid="knowledge-context">
        <strong>What would an agent be given</strong>
        <p className="hint">Choose an agent and what is in play (one subject a line, such as <span className="mono">semantic-model:Sales/measure:revenue</span>). The same as <span className="mono">knowledge context</span> will print.</p>
        <select value={agent} onChange={(e) => setAgent(e.target.value)} data-testid="context-agent" aria-label="Agent">
          <option value="">Choose an agent…</option>
          {agents.data?.map((a) => <option key={a.name} value={a.name}>{a.title ?? a.name}</option>)}
        </select>
        <textarea rows={3} style={{ width: '100%', margin: '8px 0' }} value={inPlay} onChange={(e) => setInPlay(e.target.value)} placeholder="build-project:sales-warehouse" data-testid="context-in-play" aria-label="What is in play" />
        <button type="button" className="btn btn-sm btn-primary" disabled={!agent || context.isPending} onClick={() => context.mutate()} data-testid="context-show">Show the context</button>
        <ErrorBanner error={context.error} />
        {report && (
          <div data-testid="context-report" data-over={report.overBudget} style={{ marginTop: 8 }}>
            <div>{report.totalTokens} tokens given, against a budget of {report.budgetTokens} ({percent(report.budgetTokens / report.window)} of {report.window}); counted with {report.basis}.{report.overBudget ? ' Over the budget.' : ''}</div>
            <ol>{report.shares.map((s, i) => <li key={i} data-testid="context-part">{contextLine(s)}</li>)}</ol>
          </div>
        )}
      </div></div>
    </div>
  )
}

// ---- KN37 ----

function WorklistTab({ overview }: { overview: KnowledgeOverview }) {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const [, setParams] = useSearchParams()
  const [repointing, setRepointing] = useState<string | null>(null)
  const [target, setTarget] = useState('')
  const [dropType, setDropType] = useState('semantic-model')
  const [dropName, setDropName] = useState('')
  const refresh = (data: KnowledgeOverview) => client.setQueryData(['knowledge', 'overview'], data)
  const repoint = useMutation({ mutationFn: ({ item, subject }: { item: WorkItem; subject: string }) => knowledgeApi.repoint(itemProject(item, overview), item.itemId, subject, target), onSuccess: (d) => { refresh(d); setRepointing(null); setTarget('') } })
  const retire = useMutation({ mutationFn: (item: WorkItem) => knowledgeApi.retire(itemProject(item, overview), item.itemId), onSuccess: () => client.invalidateQueries({ queryKey: ['knowledge'] }) })
  const subject = useMutation({ mutationFn: (status: 'exists' | 'gone') => knowledgeApi.setSubject(dropType, dropName, status), onSuccess: refresh })
  const fromLineage = useMutation({ mutationFn: () => knowledgeApi.refreshSubjects(), onSuccess: (r) => refresh(r.overview) })
  return (
    <div data-testid="knowledge-worklist">
      <ErrorBanner error={repoint.error ?? retire.error ?? subject.error ?? fromLineage.error} />
      {!overview.hasSubjectCatalog && <div className="hint" data-testid="worklist-no-catalog">No catalog says what exists yet, so nothing can be orphaned. Read it from the lineage graph or say what exists below.</div>}
      {overview.worklists.length === 0 && <div className="empty" data-testid="worklist-empty">Nothing needs an owner's attention.</div>}
      {overview.worklists.map((l) => (
        <div key={l.owner} className="card" style={{ marginBottom: 12 }} data-testid="worklist" data-owner={l.owner}>
          <div className="card-body">
            <strong>{l.owner}</strong> <span className="hint">{l.work.length} to look at</span>
            {l.work.map((w) => (
              <div key={`${w.kind}-${w.itemId}`} style={{ margin: '8px 0' }} data-testid="work-item" data-kind={w.kind} data-item={w.itemId}>
                <div><span className="mono">{w.itemId}</span> · {workWord(w.kind)}</div>
                <div className="hint">{w.message}</div>
                {isAdmin && workActions(w).includes('repoint') && w.subject && (repointing === w.itemId
                  ? (
                    <span>
                      <input value={target} onChange={(e) => setTarget(e.target.value)} placeholder="the object it is about now" data-testid="repoint-target" aria-label="New name" />{' '}
                      <button type="button" className="btn btn-sm btn-primary" disabled={!target.trim() || repoint.isPending} onClick={() => repoint.mutate({ item: w, subject: w.subject! })} data-testid="repoint-save">Point it there</button>{' '}
                      <button type="button" className="btn btn-sm" onClick={() => setRepointing(null)}>Cancel</button>
                    </span>
                  )
                  : <button type="button" className="btn btn-sm" onClick={() => setRepointing(w.itemId)} data-testid="work-repoint">Point at another object</button>)}{' '}
                {isAdmin && workActions(w).includes('retire') && <button type="button" className="btn btn-sm" disabled={retire.isPending} onClick={() => retire.mutate(w)} data-testid="work-retire">Retire the item</button>}{' '}
                <button type="button" className="btn btn-sm" onClick={() => setParams({ tab: 'items', item: `${itemProject(w, overview)}/${w.itemId}` })} data-testid="work-open">Open</button>
              </div>
            ))}
          </div>
        </div>
      ))}
      {isAdmin && (
        <div className="card"><div className="card-body" data-testid="subject-catalog">
          <strong>What exists to be about</strong>
          <p className="hint">What the lineage graph of the servers has a node for exists (semantic models and their measures and dimensions, apps, integrations, Store models, connections); one it showed before and no longer shows is gone, when every server answered. What an administrator says is never changed by it. A model that is gone orphans its items.</p>
          <button type="button" className="btn btn-sm btn-primary" disabled={fromLineage.isPending} onClick={() => fromLineage.mutate()} data-testid="subjects-from-lineage">Read from the lineage graph</button>
          {fromLineage.data && <div className="hint" data-testid="subjects-from-lineage-result" data-complete={fromLineage.data.complete}>{refreshLine(fromLineage.data)}</div>}
          <table className="table"><tbody>
            {overview.subjects.map((s) => <tr key={`${s.type}:${s.name}:${s.narrowingKind}:${s.narrowingName}`} data-testid="subject" data-status={s.status}><td className="mono">{s.type}:{s.name}{s.narrowingKind ? `/${s.narrowingKind}:${s.narrowingName}` : ''}</td><td>{s.status}</td><td className="hint">{subjectSource(s)}</td></tr>)}
          </tbody></table>
          <input value={dropType} onChange={(e) => setDropType(e.target.value)} aria-label="Type" data-testid="subject-type" style={{ width: 160 }} />{' '}
          <input value={dropName} onChange={(e) => setDropName(e.target.value)} aria-label="Name" placeholder="name" data-testid="subject-name" />{' '}
          <button type="button" className="btn btn-sm" disabled={!dropName.trim()} onClick={() => subject.mutate('exists')} data-testid="subject-exists">It exists</button>{' '}
          <button type="button" className="btn btn-sm" disabled={!dropName.trim()} onClick={() => subject.mutate('gone')} data-testid="subject-gone">It is gone</button>
        </div></div>
      )}
    </div>
  )
}

/** Which project an item of a worklist is in: the project whose file list has it. */
function itemProject(item: WorkItem, overview: KnowledgeOverview): string {
  return overview.projects.find((p) => p.files.some((f) => f.id === item.itemId))?.name ?? 'project'
}

// ---- KN38 and the item editor ----

function ItemsTab({ overview }: { overview: KnowledgeOverview }) {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const [params, setParams] = useSearchParams()
  const open = params.get('item')
  const files = overview.projects.flatMap((p) => p.files.map((f) => ({ project: p.name, ...f })))
  const [project, id] = open ? [open.split('/')[0], open.split('/').slice(1).join('/')] : ['', '']
  const item = useQuery({ queryKey: ['knowledge', 'item', project, id], queryFn: () => knowledgeApi.item(project, id), enabled: open !== null })
  const [text, setText] = useState('')
  const [loaded, setLoaded] = useState<string | null>(null)
  const [preview, setPreview] = useState<{ preview: ChunkPreview; reaches: Reach[] } | null>(null)
  const [saved, setSaved] = useState(false)
  useEffect(() => { if (item.data && loaded !== `${project}/${id}`) { setText(item.data.text); setLoaded(`${project}/${id}`); setSaved(false) } }, [item.data, loaded, project, id])
  useEffect(() => {
    if (!open || loaded !== `${project}/${id}`) return
    const handle = setTimeout(() => { knowledgeApi.previewChunks(project, id, text).then(setPreview).catch(() => setPreview(null)) }, 350)
    return () => clearTimeout(handle)
  }, [text, project, id, open, loaded])
  const save = useMutation({ mutationFn: () => knowledgeApi.save(project, id, text), onSuccess: async () => { setSaved(true); await client.invalidateQueries({ queryKey: ['knowledge'] }) } })
  const errors = preview?.preview.diagnostics.filter((d) => d.severity === 'error') ?? []

  return (
    <div className="row" style={{ gap: 24, alignItems: 'flex-start', flexWrap: 'wrap' }} data-testid="knowledge-items">
      <div className="card" style={{ minWidth: 240, flex: '0 1 280px' }}><div className="card-body">
        <strong>Items</strong>
        {files.length === 0 && <div className="hint" data-testid="items-none">No item yet.</div>}
        {files.map((f) => (
          <div key={`${f.project}/${f.id}`}>
            <button type="button" className={`btn btn-sm${open === `${f.project}/${f.id}` ? ' btn-primary' : ''}`} style={{ margin: '2px 0', textAlign: 'left' }} onClick={() => setParams({ tab: 'items', item: `${f.project}/${f.id}` })} data-testid={`item-${f.project}-${f.id}`}>
              {f.id} · {f.role}
            </button>
          </div>
        ))}
        <ScopeBox />
      </div></div>
      <div style={{ flex: '1 1 520px', minWidth: 0 }}>
        {!open && <div className="hint">Choose an item to edit it and see how it would be split.</div>}
        {open && (
          <div data-testid="item-editor" data-item={open}>
            <h2 className="mono">{open}</h2>
            <ErrorBanner error={item.error ?? save.error} />
            <CodeEditor value={text} language="plaintext" onChange={(v) => { setText(v); setSaved(false) }} diagnostics={preview?.preview.diagnostics.map((d) => ({ line: d.line, column: d.column, message: d.message, severity: d.severity === 'error' ? 'error' as const : 'warning' as const }))} readOnly={!isAdmin} minLines={12} testId="item-text" />
            <div data-testid="item-diagnostics">
              {preview?.preview.diagnostics.map((d, i) => <div key={i} data-testid="item-diagnostic" data-severity={d.severity}><span className="mono">{d.code}</span> line {d.line}: {d.message}{d.fix ? ` ${d.fix}` : ''}</div>)}
            </div>
            {isAdmin && <button type="button" className="btn btn-sm btn-primary" disabled={errors.length > 0 || save.isPending} onClick={() => save.mutate()} data-testid="item-save">Save</button>}
            {saved && <span className="hint" data-testid="item-saved"> Saved.</span>}
            {preview && (
              <div className="card" style={{ marginTop: 12 }}><div className="card-body" data-testid="chunk-preview" data-changed={preview.preview.changedChunks}>
                <strong>How it will be split</strong>{' '}
                <span className="hint">{preview.preview.chunks.length} chunk{preview.preview.chunks.length === 1 ? '' : 's'}{preview.preview.changedChunks > 0 ? `, ${preview.preview.changedChunks} changed since the saved version` : ''}. Only a changed chunk is embedded again.</span>
                <table className="table"><tbody>
                  {preview.preview.chunks.map((c) => (
                    <tr key={c.id} data-testid="chunk" data-changed={c.changed}>
                      <td>{c.headingPath.length > 0 ? c.headingPath.join(' > ') : '(the whole item)'}</td><td>{c.tokens} tokens</td><td>line {c.startLine}</td><td>{c.changed ? <strong data-testid="chunk-changed">changed</strong> : ''}</td>
                    </tr>
                  ))}
                </tbody></table>
                {preview.reaches.length > 0 && (
                  <div data-testid="item-reaches">
                    <strong>Agents it reaches</strong>
                    {preview.reaches.map((r) => (
                      <div key={r.agent} data-testid="reach" data-agent={r.agent} data-over={r.pushesOver}>
                        {r.agent}: adds {r.adds} tokens to what is always given{r.budget !== null ? ` (${r.used} of ${r.budget} used)` : ''}{r.pushesOver ? ' — that pushes it over its budget.' : ''}
                      </div>
                    ))}
                  </div>
                )}
              </div></div>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

function ScopeBox() {
  const [inPlay, setInPlay] = useState('')
  const [audience, setAudience] = useState('back-end')
  const [agent, setAgent] = useState('')
  const [result, setResult] = useState<ScopePreview | null>(null)
  const run = useMutation({ mutationFn: () => knowledgeApi.previewScope(parseInPlay(inPlay), audience, agent), onSuccess: (r) => setResult(r.preview) })
  return (
    <div style={{ marginTop: 16 }} data-testid="scope-box">
      <strong>What would be given</strong>
      <textarea rows={3} style={{ width: '100%', margin: '6px 0' }} value={inPlay} onChange={(e) => setInPlay(e.target.value)} placeholder="what is in play, one subject a line" data-testid="scope-in-play" aria-label="What is in play" />
      <select value={audience} onChange={(e) => setAudience(e.target.value)} data-testid="scope-audience" aria-label="Audience"><option value="back-end">back-end agent</option><option value="front-end">front-end agent</option></select>{' '}
      <input value={agent} onChange={(e) => setAgent(e.target.value)} placeholder="agent name" aria-label="Agent" data-testid="scope-agent" style={{ width: 120 }} />{' '}
      <button type="button" className="btn btn-sm" onClick={() => run.mutate()} data-testid="scope-show">Show</button>
      <ErrorBanner error={run.error} />
      {result && (
        <div data-testid="scope-result">
          <div className="hint">In the order they are given, broad to narrow.</div>
          <ol>{result.instructions.map((i) => <li key={i.id} data-testid="scope-instruction">{i.id} <span className="hint">({i.scope})</span></li>)}</ol>
          {result.guardrails.length > 0 && <div><strong>Guardrails</strong><ol>{result.guardrails.map((i) => <li key={i.id} data-testid="scope-guardrail">{i.id} <span className="hint">({i.scope})</span></li>)}</ol></div>}
          {result.skipped.length > 0 && <div><strong>Left out</strong>{result.skipped.map((s) => <div key={s.id} data-testid="scope-skipped" data-item={s.id}>{s.id}: <span className="hint">{s.reason}</span></div>)}</div>}
        </div>
      )}
    </div>
  )
}

// ---- KN36 ----

function InstallTab({ overview }: { overview: KnowledgeOverview }) {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const install = overview.projects.find((p) => p.name === 'install')
  const [title, setTitle] = useState('')
  const [body, setBody] = useState('')
  const [audience, setAudience] = useState<'everyone' | 'back-end' | 'front-end'>('everyone')
  const [result, setResult] = useState<{ reaches: Reach[]; refused: string[] } | null>(null)
  const create = useMutation({ mutationFn: knowledgeApi.createInstall, onSuccess: (d) => client.setQueryData(['knowledge', 'overview'], d) })
  const id = policyItemId(title)
  const text = policyItemText(body, audience)
  const add = useMutation({
    mutationFn: async () => {
      const reach = await knowledgeApi.previewChunks('install', id, text)
      try { await knowledgeApi.save('install', id, text) } catch (e) { setResult({ reaches: reach.reaches, refused: [(e as Error).message] }); throw e }
      return reach
    },
    onSuccess: async (reach) => { setResult({ reaches: reach.reaches, refused: [] }); setTitle(''); setBody(''); await client.invalidateQueries({ queryKey: ['knowledge'] }) },
  })
  const policyFiles = install?.files.filter((f) => isPolicyItem('install', f.id)) ?? []
  return (
    <div data-testid="knowledge-install">
      <p className="hint">The install's own guardrails, given to every agent: text only. Settings (what asks, limits, data rules) belong in the install policy under Agents, not in a file here; a <span className="mono">prompting</span>, <span className="mono">limits</span> or <span className="mono">data-rules</span> key in an item is refused and says so.</p>
      <ErrorBanner error={create.error} />
      {!install?.exists && (
        <div data-testid="install-missing">
          <div className="empty">The install knowledge project has not been made.</div>
          {isAdmin && <button type="button" className="btn btn-sm btn-primary" disabled={create.isPending} onClick={() => create.mutate()} data-testid="install-create">Make it from the template</button>}
        </div>
      )}
      {install?.exists && (
        <>
          <table className="table" data-testid="install-files">
            <thead><tr><th>Item</th><th>Role</th><th>Owner</th></tr></thead>
            <tbody>{install.files.map((f) => <tr key={f.id} data-testid="install-file" data-policy={isPolicyItem('install', f.id)}><td className="mono">{f.id}</td><td>{f.role}</td><td>{f.owner ?? ''}</td></tr>)}</tbody>
          </table>
          <div className="hint" data-testid="install-policy-count">{policyFiles.length} guardrail{policyFiles.length === 1 ? '' : 's'} in <span className="mono">policy/</span>.</div>
          {isAdmin && (
            <div className="card" style={{ marginTop: 12 }}><div className="card-body" data-testid="policy-item-form">
              <strong>A new guardrail</strong>
              <p className="hint">It is saved as <span className="mono">{id}</span>, a guardrail owned by <span className="mono">administrators</span>; the form fixes both.</p>
              <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Title" aria-label="Title" data-testid="policy-item-title" style={{ width: '100%' }} />
              <textarea rows={5} value={body} onChange={(e) => setBody(e.target.value)} placeholder="What agents must always do or never do" aria-label="Text" data-testid="policy-item-body" style={{ width: '100%', margin: '6px 0' }} />
              <select value={audience} onChange={(e) => setAudience(e.target.value as typeof audience)} data-testid="policy-item-audience" aria-label="For"><option value="everyone">every agent</option><option value="back-end">back-end agents</option><option value="front-end">front-end agents</option></select>{' '}
              <button type="button" className="btn btn-sm btn-primary" disabled={!title.trim() || !body.trim() || add.isPending} onClick={() => add.mutate()} data-testid="policy-item-save">Add the guardrail</button>
              {result?.refused.map((r, i) => <div key={i} className="banner error" data-testid="policy-item-refused">{r}</div>)}
              {result && result.refused.length === 0 && (
                <div data-testid="policy-item-reach">
                  {result.reaches.length === 0 && <div className="hint">It reaches no agent that is defined.</div>}
                  {result.reaches.map((r) => <div key={r.agent} data-testid="reach" data-agent={r.agent} data-over={r.pushesOver}>Reaches {r.agent}: adds {r.adds} tokens{r.pushesOver ? ', which pushes it over its budget.' : '.'}</div>)}
                </div>
              )}
            </div></div>
          )}
        </>
      )}
    </div>
  )
}
