import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import { fileFailure } from './FileTools'

/** The engines the tool builds on, which can hold models, and a DuckDB file as a read-only origin. (ODBC and Flight SQL origins are written in the file by hand.) */
export const CONNECTION_ENGINES: { key: string; label: string; origin?: boolean }[] = [
  { key: 'sqlserver', label: 'SQL Server' },
  { key: 'postgres', label: 'PostgreSQL' },
  { key: 'fabric', label: 'Microsoft Fabric' },
  // a file the tool only reads, to import tables from or copy from: no address (the server’s login for it is the file’s path), and never written to
  { key: 'duckdb', label: 'DuckDB file (read-only origin)', origin: true },
]

export interface ConnectionForm {
  name: string
  engine: string
  encrypt: boolean
  trustCertificate: boolean
}

export const emptyConnection: ConnectionForm = { name: '', engine: 'sqlserver', encrypt: true, trustCertificate: false }

const NAME = /^[a-z][a-z0-9_]*$/

/** What is wrong with the form, as a sentence under it; null while it is empty or fine. */
export function connectionProblem(form: ConnectionForm): string | null {
  if (form.name !== '' && !NAME.test(form.name)) return 'A connection is named with lower-case letters, digits and underscores, starting with a letter, for example warehouse.'
  return null
}

const isOrigin = (form: ConnectionForm) => CONNECTION_ENGINES.find((e) => e.key === form.engine)?.origin === true

export const connectionReady = (form: ConnectionForm) => form.name !== '' && connectionProblem(form) === null

export const connectionPath = (form: ConnectionForm) => `connections/${form.name}.yaml`

/**
 * The `connection/1` file for the form (one file per connection, named like it). A Build connection file names the engine and how the connection is secured and nothing else: where the database is and who logs in are the
 * server's own (`DBDATA_BUILD_<NAME>_READ` and `_WRITE`, an address and a login in one string), so the file can be committed and reviewed.
 */
export function connectionYaml(form: ConnectionForm): string {
  if (isOrigin(form)) return ['schema: connection/1', `name: ${form.name}`, `engine: ${form.engine}`, 'access: readonly', ''].join('\n')
  return [
    'schema: connection/1',
    `name: ${form.name}`,
    `engine: ${form.engine}`,
    'tls:',
    `  encrypt: ${form.encrypt}`,
    `  trust_server_certificate: ${form.trustCertificate}`,
    '',
  ].join('\n')
}

export interface NewConnectionViewProps {
  form: ConnectionForm
  busy: boolean
  failure: string | null
  onChange: (form: ConnectionForm) => void
  onCreate: () => void
  onCancel: () => void
}

export function NewConnectionView(p: NewConnectionViewProps) {
  const { form } = p
  const problem = connectionProblem(form)
  const set = (patch: Partial<ConnectionForm>) => p.onChange({ ...form, ...patch })
  const engine = CONNECTION_ENGINES.find((e) => e.key === form.engine)
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="build-new-connection">
      <div className="card-body">
        <div className="row" style={{ gap: 12, flexWrap: 'wrap', alignItems: 'baseline' }}>
          <input value={form.name} onChange={(e) => set({ name: e.target.value })} placeholder="name, for example warehouse" aria-label="Connection name" data-testid="build-new-connection-name" />
          <select value={form.engine} onChange={(e) => set({ engine: e.target.value })} aria-label="Engine" data-testid="build-new-connection-engine">
            {CONNECTION_ENGINES.map((e) => <option key={e.key} value={e.key}>{e.label}</option>)}
          </select>
        </div>
        <div className="row" style={{ gap: 16, marginTop: 8, flexWrap: 'wrap' }}>
          {!engine?.origin && <label className="row" style={{ gap: 4 }}><input type="checkbox" checked={form.encrypt} onChange={(e) => set({ encrypt: e.target.checked })} data-testid="build-new-connection-encrypt" /> Encrypt the connection</label>}
          {!engine?.origin && <label className="row" style={{ gap: 4 }}><input type="checkbox" checked={form.trustCertificate} onChange={(e) => set({ trustCertificate: e.target.checked })} data-testid="build-new-connection-trust" /> Trust the server’s certificate without checking it</label>}
          <button type="button" className="btn btn-primary" disabled={!connectionReady(form) || p.busy} onClick={p.onCreate} data-testid="build-new-connection-create">{p.busy ? 'Making…' : 'Make the connection'}</button>
          <button type="button" className="btn" onClick={p.onCancel} data-testid="build-new-connection-cancel">Cancel</button>
        </div>
        <div className="hint" style={{ marginTop: 6 }}>The file holds no address and no login: the server holds them, in one string for each of the read and the write login, in its own secrets or environment (<span className="mono">DBDATA_BUILD_{(form.name || 'NAME').toUpperCase()}_READ</span> and <span className="mono">_WRITE</span>), {engine?.origin ? <> For a DuckDB file the login is the path of the file.</> : null} The file can be reviewed and committed.</div>
        {problem && <div className="hint" style={{ marginTop: 6 }} data-testid="build-new-connection-problem">{problem}</div>}
        {p.failure && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="build-new-connection-failed"><span className="mark">!</span><span>{p.failure}</span></div>}
      </div>
    </div>
  )
}

/** "New connection" in the Edit tab: writes `connections/<name>.yaml` in the workspace, then opens it. */
export function NewConnectionPanel({ project, workspace, onCreated, onCancel }: { project: string; workspace: string; onCreated: (path: string) => void; onCancel: () => void }) {
  const client = useQueryClient()
  const [form, setForm] = useState<ConnectionForm>(emptyConnection)
  const [failure, setFailure] = useState<string | null>(null)
  const create = useMutation({
    mutationFn: () => buildApi.createFile(project, workspace, connectionPath(form), connectionYaml(form)),
    onSuccess: async (answer) => {
      const problem = fileFailure(answer, 'The connection could not be made.')
      setFailure(problem)
      if (problem) return
      await client.invalidateQueries({ queryKey: ['build', project, workspace] })
      onCreated(connectionPath(form))
    },
  })
  return (
    <>
      <NewConnectionView form={form} busy={create.isPending} failure={failure} onChange={(next) => { setForm(next); setFailure(null) }} onCreate={() => create.mutate()} onCancel={onCancel} />
      <ErrorBanner error={create.error} />
    </>
  )
}
