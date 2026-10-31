import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, ErrorBanner, useNavigate } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { ProjectTemplateInfo } from '../types'

/** What a project's name may be, as the server checks it: lower-case letters, digits, `-` and `_`, starting with a letter or digit. */
export const PROJECT_NAME = /^[a-z0-9][a-z0-9_-]{0,63}$/

/** The sentence under the name box: nothing while it is fine or empty. */
export function projectNameProblem(name: string): string | null {
  if (name === '' || PROJECT_NAME.test(name)) return null
  return 'A project name has lower-case letters, digits, "-" and "_", and starts with a letter or digit.'
}

/** What the server said when it would not make the project, in its own words (a 403 is the role). */
export function newProjectFailure(error: unknown): string | null {
  if (!error) return null
  if (error instanceof ApiError) return error.status === 403 ? 'Making a project needs the author role on this server.' : error.message
  return 'The server could not be reached, so no project was made.'
}

export interface NewProjectViewProps {
  templates: ProjectTemplateInfo[]
  template: string
  name: string
  busy: boolean
  failure: string | null
  onTemplate: (name: string) => void
  onName: (name: string) => void
  onCreate: () => void
  onCancel: () => void
}

/** Choosing a template, naming the project, making it. A template has no questions of its own: its models, tests and README are written as they are, and the Edit tab changes them. */
export function NewProjectView(p: NewProjectViewProps) {
  const problem = projectNameProblem(p.name)
  const ready = p.template !== '' && p.name !== '' && problem === null && !p.busy
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="build-new-project">
      <div className="card-body">
        <div className="hint" style={{ marginBottom: 8 }}>Start from a template. The server makes the project as a git repository with the template as its first commit, and hosts it.</div>
        <div role="radiogroup" aria-label="Template" style={{ display: 'grid', gap: 6, marginBottom: 12 }}>
          {p.templates.map((t) => (
            <label key={t.name} className="row" style={{ gap: 10, alignItems: 'baseline' }} data-testid={`build-new-project-template-${t.name}`}>
              <input type="radio" name="template" value={t.name} checked={p.template === t.name} onChange={() => p.onTemplate(t.name)} />
              <span><span className="mono">{t.name}</span> <span className="hint">({t.files} files)</span><br /><span className="hint">{t.description}</span></span>
            </label>
          ))}
        </div>
        <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
          <input value={p.name} onChange={(e) => p.onName(e.target.value)} placeholder="project name" aria-label="Project name" data-testid="build-new-project-name" />
          <button type="button" className="btn btn-primary" disabled={!ready} onClick={p.onCreate} data-testid="build-new-project-create">{p.busy ? 'Making…' : 'Create the project'}</button>
          <button type="button" className="btn" onClick={p.onCancel} data-testid="build-new-project-cancel">Cancel</button>
        </div>
        {problem && <div className="hint" style={{ marginTop: 6 }} data-testid="build-new-project-name-problem">{problem}</div>}
        {p.failure && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="build-new-project-failed"><span className="mark">!</span><span>{p.failure}</span></div>}
      </div>
    </div>
  )
}

/** The Projects page's "New project": shown only to someone the server lets make one. */
export function NewProjectPanel() {
  const client = useQueryClient()
  const navigate = useNavigate()
  const [open, setOpen] = useState(false)
  const [template, setTemplate] = useState('')
  const [name, setName] = useState('')
  const catalog = useQuery({ queryKey: ['build', 'templates'], queryFn: buildApi.templates })
  const create = useMutation({
    mutationFn: () => buildApi.createProject(name, template),
    onSuccess: async (made) => {
      await client.invalidateQueries({ queryKey: ['build', 'projects'] })
      setOpen(false)
      setName('')
      navigate(`/projects/${encodeURIComponent(made.id)}`)
    },
  })
  if (!catalog.data?.can_create) return <ErrorBanner error={catalog.error} />
  return (
    <>
      {!open && <div style={{ marginBottom: 14 }}><button type="button" className="btn btn-primary" onClick={() => setOpen(true)} data-testid="build-new-project-open">New project</button></div>}
      {open && (
        <NewProjectView
          templates={catalog.data.templates}
          template={template}
          name={name}
          busy={create.isPending}
          failure={newProjectFailure(create.error)}
          onTemplate={setTemplate}
          onName={(value) => { setName(value); create.reset() }}
          onCreate={() => create.mutate()}
          onCancel={() => { setOpen(false); create.reset() }}
        />
      )}
    </>
  )
}
