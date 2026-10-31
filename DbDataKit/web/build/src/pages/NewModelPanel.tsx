import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { CommandAnswer } from '../types'

/** The kinds a model can be started as (`project model create`): a copy, a native model and a mapped model are written by hand or imported. */
export const MODEL_KINDS: { key: string; label: string; about: string }[] = [
  { key: 'view', label: 'View', about: 'A query the database runs each time it is read.' },
  { key: 'full', label: 'Full table', about: 'A table rebuilt from its query on every refresh.' },
  { key: 'incremental_by_unique_key', label: 'Incremental by key', about: 'A table that adds new rows and replaces the rows whose key matches. Starts with the key `id`.' },
  { key: 'incremental_by_time_range', label: 'Incremental by time', about: 'A table loaded one time range at a time. Starts with the time column `event_ts`.' },
]

const MODEL_NAME = /^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$/

/** The sentence under the name box: nothing while it is empty or fine. */
export function modelNameProblem(name: string): string | null {
  if (name === '' || MODEL_NAME.test(name)) return null
  return 'A model is named schema_name.object_name, with letters, digits and underscores, for example marts.fct_returns.'
}

/** The file the model's query goes in, for the project's layout (`models/<schema>/<object>.sql` unless the tool says otherwise: the server's answer names the real files). */
export function queryFileOf(files: string[]): string | undefined {
  return files.find((f) => f.endsWith('.sql'))
}

/** Why the server did not make the model, in the tool's own words. */
export function createModelFailure(answer: CommandAnswer<unknown>): string | null {
  if (answer.document?.ok) return null
  const problem = answer.document?.diagnostics?.find((d) => d.severity === 'error')
  return problem?.found ?? answer.document?.errors?.[0] ?? (answer.text ? answer.text.split('\n')[0] : null) ?? 'The model could not be made.'
}

export interface NewModelViewProps {
  name: string
  kind: string
  connections: string[]
  connection: string[]
  busy: boolean
  failure: string | null
  onName: (name: string) => void
  onKind: (kind: string) => void
  onConnection: (name: string, on: boolean) => void
  onCreate: () => void
  onCancel: () => void
}

/** A name and a kind, and the connections the model lives on when the project has several. The model starts as a definition and a query that runs; the editor and the definition panel take it from there. */
export function NewModelView(p: NewModelViewProps) {
  const problem = modelNameProblem(p.name)
  const ready = p.name !== '' && problem === null && !p.busy
  const kind = MODEL_KINDS.find((k) => k.key === p.kind)
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="build-new-model">
      <div className="card-body">
        <div className="row" style={{ gap: 12, flexWrap: 'wrap', alignItems: 'baseline' }}>
          <input value={p.name} onChange={(e) => p.onName(e.target.value)} placeholder="schema_name.object_name" aria-label="Model name" data-testid="build-new-model-name" />
          <select value={p.kind} onChange={(e) => p.onKind(e.target.value)} aria-label="Kind" data-testid="build-new-model-kind">
            {MODEL_KINDS.map((k) => <option key={k.key} value={k.key}>{k.label}</option>)}
          </select>
          <button type="button" className="btn btn-primary" disabled={!ready} onClick={p.onCreate} data-testid="build-new-model-create">{p.busy ? 'Making…' : 'Make the model'}</button>
          <button type="button" className="btn" onClick={p.onCancel} data-testid="build-new-model-cancel">Cancel</button>
        </div>
        {kind && <div className="hint" style={{ marginTop: 6 }}>{kind.about}</div>}
        {p.connections.length > 1 && (
          <div className="row" style={{ gap: 12, marginTop: 8, flexWrap: 'wrap' }} role="group" aria-label="Connections">
            <span className="hint">Lives on (none chosen: the project’s default):</span>
            {p.connections.map((c) => (
              <label key={c} className="row" style={{ gap: 4 }}><input type="checkbox" checked={p.connection.includes(c)} onChange={(e) => p.onConnection(c, e.target.checked)} data-testid={`build-new-model-connection-${c}`} /><span className="mono">{c}</span></label>
            ))}
          </div>
        )}
        {problem && <div className="hint" style={{ marginTop: 6 }} data-testid="build-new-model-name-problem">{problem}</div>}
        {p.failure && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="build-new-model-failed"><span className="mark">!</span><span>{p.failure}</span></div>}
      </div>
    </div>
  )
}

/** "New model" in the Edit tab: makes the files in the workspace, then opens the query. */
export function NewModelPanel({ project, workspace, onCreated, onCancel }: { project: string; workspace: string; onCreated: (queryFile: string | undefined) => void; onCancel: () => void }) {
  const client = useQueryClient()
  const [name, setName] = useState('')
  const [kind, setKind] = useState('view')
  const [connection, setConnection] = useState<string[]>([])
  const [failure, setFailure] = useState<string | null>(null)
  const connections = useQuery({ queryKey: ['build', project, 'connections'], queryFn: () => buildApi.connections(project) })
  const create = useMutation({
    mutationFn: () => buildApi.createModel(project, workspace, name.trim(), kind, connection),
    onSuccess: async (answer) => {
      const problem = createModelFailure(answer)
      setFailure(problem)
      if (problem) return
      await client.invalidateQueries({ queryKey: ['build', project, workspace] })
      onCreated(queryFileOf(answer.document?.data?.files ?? []))
    },
  })
  return (
    <>
      <NewModelView
        name={name}
        kind={kind}
        connections={connections.data ?? []}
        connection={connection}
        busy={create.isPending}
        failure={failure}
        onName={(value) => { setName(value); setFailure(null) }}
        onKind={setKind}
        onConnection={(c, on) => setConnection((now) => (on ? [...now, c] : now.filter((x) => x !== c)))}
        onCreate={() => create.mutate()}
        onCancel={onCancel}
      />
      <ErrorBanner error={create.error} />
    </>
  )
}
