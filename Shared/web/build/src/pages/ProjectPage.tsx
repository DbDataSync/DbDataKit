import { useMutation, useQuery } from '@tanstack/react-query'
import { Outlet, useOutletContext, useParams } from 'react-router-dom'
import { ErrorBanner, NavLink, tabClass } from '@dbdatakit/shell'
import { buildApi } from '../api'
import { BuildShell } from '../BuildShell'
import { TestsPanel } from './TestsPanel'
import { GraphView } from './GraphView'
import type { Diagnostic, GraphNode, ProjectGraph } from '../types'

interface ProjectContext {
  project: string
  graph: ProjectGraph | undefined
  loading: boolean
  problem: string | null
}

/** A project: the frame and tabs; the tabs are routes. The graph is read once and shared, because the model list and the picture are two views of it. */
export function ProjectPage() {
  const { project = '' } = useParams()
  const graph = useQuery({ queryKey: ['build', project, 'graph'], queryFn: () => buildApi.graph(project) })
  const answer = graph.data
  const problem = answer ? (answer.document?.ok ? null : (answer.document?.errors?.[0] ?? answer.text ?? 'The server could not read this project.')) : null
  const context: ProjectContext = { project, graph: answer?.document?.data, loading: graph.isLoading, problem }
  const base = `/projects/${encodeURIComponent(project)}`

  return (
    <BuildShell
      crumbs={[{ label: 'Projects', to: '/' }, { label: project, mono: true, heading: true }]}
      tabs={
        <>
          <NavLink to={base} end className={tabClass} data-testid="build-tab-models">Models</NavLink>
          <NavLink to={`${base}/graph`} className={tabClass} data-testid="build-tab-graph">Graph</NavLink>
          <NavLink to={`${base}/check`} className={tabClass} data-testid="build-tab-check">Check</NavLink>
          <NavLink to={`${base}/status`} className={tabClass} data-testid="build-tab-status">Status</NavLink>
          <NavLink to={`${base}/plans`} className={tabClass} data-testid="build-tab-plans">Plans</NavLink>
          <NavLink to={`${base}/compare`} className={tabClass} data-testid="build-tab-compare">Compare</NavLink>
          <NavLink to={`${base}/edit`} className={tabClass} data-testid="build-tab-edit">Edit</NavLink>
          <NavLink to={`${base}/publish`} className={tabClass} data-testid="build-tab-publish">Publish</NavLink>
          <NavLink to={`${base}/views`} className={tabClass} data-testid="build-tab-views">Views</NavLink>
          <NavLink to={`${base}/sample`} className={tabClass} data-testid="build-tab-sample">Sample</NavLink>
        </>
      }
    >
      <div className="pane" data-testid="build-project-page">
        <ErrorBanner error={graph.error} />
        <Outlet context={context} />
      </div>
    </BuildShell>
  )
}

const useProject = () => useOutletContext<ProjectContext>()

/** Every model of the project, by level: what is read from outside comes first, what is built from it after. */
export function ModelsTab() {
  const { graph, loading, problem } = useProject()
  if (loading) return <div className="empty">Loading…</div>
  if (problem) return <div className="empty" data-testid="build-project-problem">{problem}</div>
  const nodes = [...(graph?.nodes ?? [])].sort((a, b) => a.level - b.level || a.name.localeCompare(b.name))
  return (
    <div className="card flush" data-testid="build-models-table">
      <div className="grid-head" style={{ gridTemplateColumns: '2fr 1fr 2fr 1fr', gap: 14 }}><span>Model</span><span>Kind</span><span>File</span><span>Level</span></div>
      {nodes.length === 0 && <div className="empty">This project has no models.</div>}
      {nodes.map((node) => (
        <div key={node.name} className="grid-row" style={{ gridTemplateColumns: '2fr 1fr 2fr 1fr', gap: 14 }} data-testid={`build-model-${node.name}`}>
          <span className="mono">{node.name}</span>
          <span>{node.kind}</span>
          <span className="mono">{node.file ?? '—'}</span>
          <span>{node.level}</span>
        </div>
      ))}
    </div>
  )
}

/** The project's models as a layered picture (`GraphView`, which the Workspace draws too). */
export function GraphTab() {
  const { graph, loading, problem } = useProject()
  if (loading) return <div className="empty">Loading…</div>
  if (problem) return <div className="empty">{problem}</div>
  return <GraphView nodes={graph?.nodes ?? []} edges={graph?.edges ?? []} />
}

/** Compiles the project without writing anything and shows what the compiler found. */
export function CheckTab() {
  const { project } = useProject()
  const check = useMutation({ mutationFn: () => buildApi.check(project) })
  const document = check.data?.document
  const diagnostics: Diagnostic[] = document?.diagnostics ?? []
  const counts = document?.data?.counts
  return (
    <div data-testid="build-check">
      <div className="row" style={{ gap: 12, marginBottom: 14 }}>
        <button type="button" className="btn btn-primary" disabled={check.isPending} onClick={() => check.mutate()} data-testid="build-check-button">{check.isPending ? 'Checking…' : 'Check the project'}</button>
        <span className="hint">Compiles every model and says what the compiler found. Nothing is written.</span>
      </div>
      <ErrorBanner error={check.error} />
      {check.data && !document && <div className="empty" data-testid="build-check-failed">{check.data.text ?? 'The server could not check this project.'}</div>}
      {counts && (
        <div className="card" style={{ marginBottom: 14 }} data-testid="build-check-counts">
          <div className="card-body">
            <span data-testid="build-check-models">{counts.models} models</span>{' · '}
            <span data-testid="build-check-errors">{counts.errors} errors</span>{' · '}
            <span data-testid="build-check-warnings">{counts.warnings} warnings</span>{' · '}
            <span>{counts.notes} notes</span>
            {document?.data?.out_of_date && document.data.out_of_date.length > 0 && (
              <div className="hint" style={{ marginTop: 8 }} data-testid="build-check-out-of-date">Out of date, to be written by a compile: {document.data.out_of_date.join(', ')}</div>
            )}
          </div>
        </div>
      )}
      {document && diagnostics.length === 0 && <div className="empty" data-testid="build-check-clean">The compiler found nothing to report.</div>}
      {diagnostics.length > 0 && (
        <div className="card flush" data-testid="build-diagnostics">
          <div className="grid-head" style={{ gridTemplateColumns: '0.8fr 0.7fr 3fr 2fr', gap: 14 }}><span>Code</span><span>Severity</span><span>What</span><span>Where</span></div>
          {diagnostics.map((d, i) => (
            <div key={`${d.code}-${i}`} className="grid-row" style={{ gridTemplateColumns: '0.8fr 0.7fr 3fr 2fr', gap: 14, alignItems: 'start' }} data-testid="build-diagnostic">
              <span className="mono">{d.code}</span>
              <span data-severity={d.severity}>{d.severity}</span>
              <span>
                <strong>{d.title}</strong>
                <div className="hint">{d.found}</div>
                {d.fix && <div className="hint">Fix: {d.fix}</div>}
              </span>
              <span className="mono">{d.location.file}{d.location.line > 0 ? `:${d.location.line}` : ''}</span>
            </div>
          ))}
        </div>
      )}
      <TestsPanel project={project} />
    </div>
  )
}

export type { GraphNode }
