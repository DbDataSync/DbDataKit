import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { CodeEditor, ErrorBanner, useAuthStatus, useIsAdmin } from '@dbdatakit/shell'
import { ConsoleShell } from '../ConsoleShell'
import { agentsApi, percent } from '../agents'
import {
  checkLine, contextLine, hitPlace, placeOf, reachLine, isPolicyItem, knowledgeApi, modesOffered, parseInPlay, policyItemId, policyItemText, refreshLine, searchModeLine, subjectSource, vectorPercent, vectorProgressLine, verifiedLine, verifiedQueryId, verifiedQueryText, verifiedRuns, workActions, workWord,
  type ChunkPreview, type KnowledgeIndexState, type KnowledgeOverview, type Reach, type ScopePreview, type SearchMode, type SearchResult, type WorkItem,
} from '../knowledge'

type Tab = 'overview' | 'search' | 'worklist' | 'items' | 'check' | 'install'
const TABS: { id: Tab; label: string }[] = [
  { id: 'overview', label: 'Index and context' }, { id: 'search', label: 'Search and index' }, { id: 'worklist', label: 'Worklists' }, { id: 'items', label: 'Items and preview' }, { id: 'check', label: 'Check' }, { id: 'install', label: 'Install knowledge' },
]

/**
 * The console's knowledge screens (plan 27a KN28, KN36, KN37, KN38): what the files say and what an agent would be given (KN28), the owners' worklists of orphaned items (KN37), an item as it is typed with how it would be split and what
 * would be given for what is in play (KN38, the item editor), and the install knowledge project with the administrators' policy guardrails (KN36). The tab Search and index (KN28 with KN05, KN07, KN08) builds the index over the files, shows how it can be searched (words, vectors, both), the embedding connection and model, the progress of the vectors and the chunks that failed, and searches it.
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
        {overview.data && tab === 'search' && <SearchTab />}
        {overview.data && tab === 'worklist' && <WorklistTab overview={overview.data} />}
        {overview.data && tab === 'items' && <ItemsTab overview={overview.data} />}
        {overview.data && tab === 'check' && <CheckTab />}
        {overview.data && tab === 'install' && <InstallTab overview={overview.data} />}
      </div>
    </ConsoleShell>
  )
}

// ---- KN10, KN17 ----
function CheckTab() {
  const check = useQuery({ queryKey: ['knowledge', 'check'], queryFn: knowledgeApi.check })
  return (
    <div data-testid="knowledge-check">
      <p className="hint">What a publish would check, before it is made: every file with the strict reader, every subject against what exists, every owner, and for each agent a project reaches what it adds to the text the agent is always given, against its budget. The command line says the same: <span className="mono">dbdatakit knowledge check</span>. A project is published with <span className="mono">knowledge publish</span> to the serving group; it is refused, naming file and line, with any error shown here.</p>
      <div><button type="button" className="btn btn-sm" onClick={() => check.refetch()} disabled={check.isFetching} data-testid="check-run">Check again</button></div>
      <ErrorBanner error={check.error} />
      {check.data?.projects.length === 0 && <div className="empty" data-testid="check-empty">There is no knowledge project to check yet. Make the install project on the Install knowledge tab, or put files in the <span className="mono">knowledge</span> folder.</div>}
      {check.data?.projects.map((p) => (
        <div key={p.name} className="card" style={{ margin: '12px 0' }} data-testid="check-project" data-project={p.name} data-ok={p.ok}>
          <div className="card-body">
            <strong>{p.name === 'install' ? 'Install knowledge' : 'Knowledge'}</strong> <span className={p.ok ? 'hint' : 'error-text'} data-testid="check-verdict">{checkLine(p)}</span>
            {p.diagnostics.map((d, i) => (
              <div key={i} style={{ margin: '6px 0' }} data-testid="check-diagnostic" data-code={d.code} data-severity={d.severity}>
                <span className="mono">{placeOf(d)}</span> <span className="hint">{d.severity} {d.code}</span>
                <div>{d.message}{d.fix ? ` ${d.fix}` : ''}</div>
              </div>
            ))}
            {p.reaches.map((r) => (
              <div key={r.agent} className={r.pushesOver ? 'error-text' : 'hint'} style={{ margin: '4px 0' }} data-testid="check-reach" data-agent={r.agent} data-over={r.pushesOver}>{reachLine(r)}</div>
            ))}
            {p.reaches.length === 0 && p.diagnostics.length === 0 && <div className="hint">No agent is given anything of this project always, and nothing is wrong with it.</div>}
          </div>
        </div>
      ))}
    </div>
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
        {isAdmin && <NewVerifiedQuery onMade={(id) => setParams({ tab: 'items', item: `project/${id}` })} />}
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
            {preview?.preview.verified && (
              <div className="card" style={{ marginTop: 12 }}><div className="card-body" data-testid="verified-preview">
                <strong>A verified query</strong>{' '}
                <span className="hint" data-testid="verified-line">{verifiedLine(preview.preview.verified)}</span>
                <div className="hint">Asked of {preview.preview.verified.model}; it runs {verifiedRuns(preview.preview.verified)}, as whoever runs it, under their own rules. Only the questions are embedded.</div>
                <ul data-testid="verified-questions">{preview.preview.verified.questions.map((q) => <li key={q}>{q}</li>)}</ul>
                {preview.preview.verified.request && <pre className="mono" data-testid="verified-request" style={{ whiteSpace: 'pre-wrap' }}>{preview.preview.verified.request}</pre>}
                {preview.preview.verified.semanticSql && <div><div className="hint">Semantic SQL, kept for reading (the request is the truth):</div><pre className="mono" data-testid="verified-sql" style={{ whiteSpace: 'pre-wrap' }}>{preview.preview.verified.semanticSql}</pre></div>}
              </div></div>
            )}
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

/** Starts a verified query as a file of the console's knowledge project from the question, the model and the request a person pastes; the strict reader's diagnostics then say what is still wrong (plan 27a KN21). */
function NewVerifiedQuery({ onMade }: { onMade: (id: string) => void }) {
  const client = useQueryClient()
  const { data: status } = useAuthStatus()
  const [question, setQuestion] = useState('')
  const [model, setModel] = useState('')
  const [request, setRequest] = useState('')
  const make = useMutation({
    mutationFn: async () => {
      const id = verifiedQueryId(question)
      await knowledgeApi.save('project', id, verifiedQueryText({ question, model, request, by: status?.displayName ?? status?.userId ?? 'an administrator', today: new Date().toISOString().slice(0, 10) }))
      return id
    },
    onSuccess: async (id) => { await client.invalidateQueries({ queryKey: ['knowledge'] }); setQuestion(''); onMade(id) },
  })
  return (
    <div style={{ marginTop: 16 }} data-testid="new-verified-query">
      <strong>A verified query</strong>
      <div className="hint">A question and the request that answers it, kept so that an agent finds it by how the question is asked. You are named as the person who verified it.</div>
      <input value={question} onChange={(e) => setQuestion(e.target.value)} placeholder="the question, as people ask it" aria-label="Question" data-testid="vq-question" style={{ width: '100%', margin: '4px 0' }} />
      <input value={model} onChange={(e) => setModel(e.target.value)} placeholder="the semantic model, for example Sales" aria-label="Model" data-testid="vq-model" style={{ width: '100%', margin: '4px 0' }} />
      <textarea rows={3} value={request} onChange={(e) => setRequest(e.target.value)} placeholder='the request as JSON: {"kind":"query","query":{"measures":["revenue"],"limit":10}}' aria-label="Request" data-testid="vq-request" style={{ width: '100%', margin: '4px 0' }} />
      <button type="button" className="btn btn-sm" disabled={!question.trim() || !model.trim() || make.isPending} onClick={() => make.mutate()} data-testid="vq-make">Start the item</button>
      <ErrorBanner error={make.error} />
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

// ---- KN28 with KN05, KN07, KN08: the index, its embedding model and a search ----

const MODE_WORDS: Record<SearchMode, string> = { words: 'Words', vectors: 'Vectors (meaning)', both: 'Both' }

function SearchTab() {
  const client = useQueryClient()
  const isAdmin = useIsAdmin()
  const index = useQuery({ queryKey: ['knowledge', 'index'], queryFn: knowledgeApi.index, refetchInterval: (q) => (q.state.data?.running ? 1500 : 6000) })
  const models = useQuery({ queryKey: ['agents', 'models'], queryFn: agentsApi.models })
  const state = index.data
  const set = (d: KnowledgeIndexState) => client.setQueryData(['knowledge', 'index'], d)
  const build = useMutation({ mutationFn: knowledgeApi.build, onSuccess: (r) => { set(r.view); client.invalidateQueries({ queryKey: ['knowledge', 'overview'] }) } })
  const retry = useMutation({ mutationFn: knowledgeApi.retry, onSuccess: set })
  const rebuild = useMutation({ mutationFn: knowledgeApi.rebuild, onSuccess: set })
  return (
    <div data-testid="knowledge-search-tab">
      <ErrorBanner error={index.error ?? build.error ?? retry.error ?? rebuild.error} />
      {state && (
        <div data-testid="index-panel" data-opened={state.opened} data-running={state.running}>
          <div className="banner" data-testid="index-message"><span>{state.message}</span></div>
          <table className="table" data-testid="index-facts">
            <tbody>
              <tr><th scope="row">Words</th><td data-testid="index-words">{state.opened ? `found with the engine's own text search (${state.wordsMode})` : '-'}</td></tr>
              <tr><th scope="row">Search</th><td data-testid="index-modes" data-vectors={state.vectorsAvailable}>{searchModeLine(state)}</td></tr>
              <tr><th scope="row">Items and chunks</th><td data-testid="index-counts">{state.opened ? `${state.items} items in ${state.chunks} chunks` : '-'}</td></tr>
              <tr><th scope="row">Vectors</th><td data-testid="index-progress" data-embedded={state.vectors?.embedded ?? 0} data-chunks={state.vectors?.chunks ?? 0} data-building={state.vectors?.buildingModel ?? ''}>
                {state.vectors ? vectorProgressLine(state.vectors) : state.opened ? 'None: ' + (state.vectorsProblem ?? 'no embedding model is bound.') : '-'}
                {state.vectors && state.vectors.chunks > 0 && <div><progress max={100} value={vectorPercent(state.vectors.buildingModel ? state.vectors.buildingEmbedded : state.vectors.embedded, state.vectors.chunks)} data-testid="index-bar" /></div>}
              </td></tr>
              <tr><th scope="row">Last background run</th><td data-testid="index-last-run">{state.running ? 'Running…' : state.lastRun ? `${state.lastRun.embedded} embedded, ${state.lastRun.failed} failed, ${state.lastRun.remaining} left${state.lastRun.switched ? '; switched to the new model' : ''}${state.lastRun.problem ? `. It stopped: ${state.lastRun.problem}` : ''}` : 'None yet in this session.'}</td></tr>
            </tbody>
          </table>
          {isAdmin && (
            <div style={{ margin: '8px 0' }}>
              <button type="button" className="btn btn-sm btn-primary" disabled={build.isPending} onClick={() => build.mutate()} data-testid="index-build">{state.opened ? 'Bring the index up to date' : 'Build the index'}</button>{' '}
              <button type="button" className="btn btn-sm" disabled={!state.vectorsAvailable || rebuild.isPending} onClick={() => rebuild.mutate()} data-testid="index-rebuild" title="Throw the vectors away and make them again; words stay">Rebuild the vectors</button>{' '}
              <button type="button" className="btn btn-sm" disabled={state.failures.length === 0 || retry.isPending} onClick={() => retry.mutate()} data-testid="index-retry">Try the failed chunks again</button>
            </div>
          )}
          <EmbeddingBox state={state} models={models.data} onChange={set} />
          {state.failures.length > 0 && (
            <div data-testid="index-failures" style={{ marginTop: 8 }}>
              <strong>Chunks that could not be embedded</strong>
              <table className="table"><thead><tr><th>Item</th><th>Chunk</th><th>Model</th><th>Tries</th><th>Why</th></tr></thead>
                <tbody>{state.failures.map((f) => <tr key={`${f.model}/${f.itemId}/${f.seq}`} data-testid="index-failure" data-item={f.itemId}><td className="mono">{f.itemId}</td><td>{f.seq}</td><td className="mono">{f.model}</td><td>{f.attempts}</td><td>{f.message}</td></tr>)}</tbody></table>
            </div>
          )}
        </div>
      )}
      {state && <SearchBox state={state} />}
    </div>
  )
}

/** The embedding connection and model of the index: what is bound, a Test that reports the dimensions, and (for an administrator) the choice. One model per index: binding another builds beside the first. */
function EmbeddingBox({ state, models, onChange }: { state: KnowledgeIndexState; models: import('../agents').ModelsView | undefined; onChange: (d: KnowledgeIndexState) => void }) {
  const isAdmin = useIsAdmin()
  const embedders = (models?.providers ?? []).filter((p) => p.purpose === 'embeddings' && p.models.length > 0)
  const [provider, setProvider] = useState('')
  const [model, setModel] = useState('')
  const chosen = embedders.find((p) => p.name === (provider || state.embedding.provider || embedders[0]?.name))
  const modelId = model || (chosen?.name === state.embedding.provider ? state.embedding.model : null) || chosen?.models[0]?.id || ''
  const bind = useMutation({ mutationFn: (v: { provider: string | null; model: string | null }) => knowledgeApi.bindEmbedding(v.provider, v.model), onSuccess: onChange })
  const test = useMutation({ mutationFn: () => agentsApi.testProvider(chosen!.name, modelId) })
  const bound = state.embedding
  return (
    <div className="card" style={{ marginTop: 8 }}><div className="card-body" data-testid="index-embedding" data-bound={bound.provider !== null} data-usable={bound.usable}>
      <strong>Embedding model</strong>
      <div data-testid="embedding-current">{bound.provider ? <>Bound to <span className="mono">{bound.provider}/{bound.model}</span>{bound.usable ? '.' : <> but it cannot be used: {bound.problem}</>}</> : 'No embedding model is bound; the index is searched by words.'}</div>
      <p className="hint">An embedding model is a connection of kind <span className="mono">model-provider</span> with purpose <span className="mono">embeddings</span> (Ollama, OpenAI, Microsoft Foundry or a compatible server), added on the Agents screen, tab Models. One model per index: binding another builds a second index beside this one, which keeps answering until the new one is complete.</p>
      {embedders.length === 0 && <div className="empty" data-testid="embedding-none">No embedding provider yet.</div>}
      {isAdmin && embedders.length > 0 && (
        <div>
          <select value={chosen?.name ?? ''} onChange={(e) => { setProvider(e.target.value); setModel('') }} data-testid="embedding-provider" aria-label="Embedding connection">{embedders.map((p) => <option key={p.name} value={p.name}>{p.name}</option>)}</select>{' '}
          <select value={modelId} onChange={(e) => setModel(e.target.value)} data-testid="embedding-model" aria-label="Embedding model">{chosen?.models.map((m) => <option key={m.id} value={m.id}>{m.id}</option>)}</select>{' '}
          <button type="button" className="btn btn-sm" disabled={!chosen || test.isPending} onClick={() => test.mutate()} data-testid="embedding-test">Test</button>{' '}
          <button type="button" className="btn btn-sm btn-primary" disabled={!chosen || bind.isPending} onClick={() => bind.mutate({ provider: chosen!.name, model: modelId })} data-testid="embedding-bind">Bind</button>{' '}
          <button type="button" className="btn btn-sm" disabled={!bound.provider || bind.isPending} onClick={() => bind.mutate({ provider: null, model: null })} data-testid="embedding-unbind">Unbind</button>
          {test.data && <div data-testid="embedding-test-result" data-ok={test.data.ok} data-dimensions={test.data.dimensions ?? ''}>{test.data.message}</div>}
          <ErrorBanner error={test.error ?? bind.error} />
        </div>
      )}
    </div></div>
  )
}

function SearchBox({ state }: { state: KnowledgeIndexState }) {
  const offered = modesOffered(state)
  const [question, setQuestion] = useState('')
  const [mode, setMode] = useState<SearchMode>('both')
  const [result, setResult] = useState<SearchResult | null>(null)
  const effective: SearchMode = offered.includes(mode) ? mode : 'words'
  const run = useMutation({ mutationFn: () => knowledgeApi.search(question, effective), onSuccess: setResult })
  return (
    <div style={{ marginTop: 16 }} data-testid="search-box">
      <strong>Search the knowledge</strong>
      <form onSubmit={(e) => { e.preventDefault(); if (question.trim()) run.mutate() }}>
        <input value={question} onChange={(e) => setQuestion(e.target.value)} placeholder="ask in your own words" style={{ width: '60%' }} aria-label="Question" data-testid="search-question" disabled={!state.opened} />{' '}
        {(['words', 'vectors', 'both'] as SearchMode[]).map((m) => (
          <label key={m} style={{ marginRight: 8 }}><input type="radio" name="search-mode" checked={effective === m} disabled={!offered.includes(m)} onChange={() => setMode(m)} data-testid={`search-mode-${m}`} /> {MODE_WORDS[m]}</label>
        ))}
        <button type="submit" className="btn btn-sm btn-primary" disabled={!state.opened || !question.trim() || run.isPending} data-testid="search-run">Search</button>
      </form>
      <ErrorBanner error={run.error} />
      {result && (
        <div data-testid="search-result" data-used={result.used} style={{ marginTop: 8 }}>
          <div className="hint" data-testid="search-used">Searched by {MODE_WORDS[result.used].toLowerCase()}.{result.note ? ` ${result.note}` : ''}</div>
          {result.hits.length === 0 && <div className="empty" data-testid="search-empty">Nothing found that you may see.</div>}
          {result.hits.map((h) => (
            <div key={h.itemId} className="card" style={{ marginTop: 6 }} data-testid="search-hit" data-item={h.itemId}><div className="card-body">
              <strong>{h.title}</strong> <span className="hint" data-testid="hit-place">{hitPlace(h)}</span>
              <div style={{ whiteSpace: 'pre-wrap' }} data-testid="hit-excerpt">{h.excerpt}</div>
              <div className="hint">from <span className="mono">{h.source}</span>, version <span className="mono">{h.version}</span></div>
            </div></div>
          ))}
        </div>
      )}
    </div>
  )
}
