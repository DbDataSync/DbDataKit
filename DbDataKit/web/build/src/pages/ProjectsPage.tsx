import { useQuery } from '@tanstack/react-query'
import { ErrorBanner, Link } from '@dbdatakit/shell'
import { buildApi } from '../api'
import { BuildShell } from '../BuildShell'
import { NewProjectPanel } from './NewProjectPanel'

/** The projects this Build server hosts. */
export function ProjectsPage() {
  const projects = useQuery({ queryKey: ['build', 'projects'], queryFn: buildApi.projects })
  return (
    <BuildShell crumbs={[{ label: 'Projects', heading: true }]}>
      <div className="pane" data-testid="build-projects-page">
        <div className="page-head">
          <h1 className="page-title">Projects</h1>
          <span className="page-note">The projects this server hosts. A project is a folder of models in a git repository; the server keeps its own working copy.</span>
        </div>
        <ErrorBanner error={projects.error} />
        <NewProjectPanel />
        <div className="card flush" data-testid="build-projects-table">
          <div className="grid-head" style={{ gridTemplateColumns: '2fr 1fr 1fr', gap: 14 }}><span>Project</span><span>From</span><span>Branch</span></div>
          {projects.isLoading && <div className="empty">Loading…</div>}
          {!projects.isLoading && (projects.data ?? []).length === 0 && <div className="empty" data-testid="build-no-projects">This server hosts no project yet. Add one with <span className="mono">server project add</span>.</div>}
          {(projects.data ?? []).map((project) => (
            <div key={project.id} className="grid-row" style={{ gridTemplateColumns: '2fr 1fr 1fr', gap: 14 }} data-testid={`build-project-${project.id}`}>
              <Link to={`/projects/${encodeURIComponent(project.id)}`} className="mono" data-testid="build-project-link">{project.name}</Link>
              <span>{project.source === 'git' ? 'a git repository' : 'a folder on the server'}</span>
              <span className="mono">{project.branch ?? '—'}</span>
            </div>
          ))}
        </div>
      </div>
    </BuildShell>
  )
}
