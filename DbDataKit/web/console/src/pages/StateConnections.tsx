import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, Field, useIsAdmin } from '@dbdatakit/shell'
import { engineName, stateApi, versionsLine, type StateConnectionRow, type StateTestResult } from '../stateApi'

const COLUMNS = '1fr 0.9fr 2.2fr 1.6fr 1.6fr 1.6fr'
const key = ['state-connections'] as const

/**
 * The state databases of the console as connections of kind `state` (plan 29b, C06): where a server's state can be moved to. A connection holds the engine, the connection string with its password as a secret reference (the password itself is typed once and kept in the
 * console's secret store), and, for SQLite, a path on the console's machine. The list says which paired servers use each one and the module versions found there; Test runs the checks of `state test` through the shared library and answers with one sentence.
 */
export function StateConnections() {
  const isAdmin = useIsAdmin()
  const client = useQueryClient()
  const list = useQuery({ queryKey: key, queryFn: stateApi.connections.list, enabled: isAdmin, refetchInterval: 30_000 })
  const [editing, setEditing] = useState<StateConnectionRow | 'new' | null>(null)
  const [tests, setTests] = useState<Record<string, StateTestResult>>({})
  const test = useMutation({
    mutationFn: (name: string) => stateApi.connections.test(name),
    onSuccess: (result, name) => setTests((all) => ({ ...all, [name]: result })),
  })
  const remove = useMutation({ mutationFn: (name: string) => stateApi.connections.remove(name), onSuccess: () => client.invalidateQueries({ queryKey: key }) })
  if (!isAdmin) return null
  const rows = list.data ?? []

  return (
    <div style={{ marginTop: 28 }} data-testid="state-connections">
      <div className="page-head">
        <h2 className="page-title" style={{ fontSize: 18 }}>State databases</h2>
        <span className="page-note">Connections of kind <span className="mono">state</span>: databases a server&apos;s state can be moved to. The password is kept in this console&apos;s secret store.</span>
      </div>
      <ErrorBanner error={list.error ?? remove.error ?? test.error} />
      {list.isSuccess && rows.length === 0 && <div className="empty" data-testid="state-connections-none">No state connection yet.</div>}
      {rows.length > 0 && (
        <div className="card flush" data-testid="state-connection-rows">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}><span>Name</span><span>Engine</span><span>Connection</span><span>Used by</span><span>Modules found</span><span /></div>
          {rows.map((row) => (
            <div key={row.name}>
              <div className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 14 }} data-testid="state-connection-row" data-name={row.name}>
                <span className="mono">{row.name}</span>
                <span>{engineName(row.engine)}</span>
                <span className="mono" style={{ overflowWrap: 'anywhere' }}>{row.connection}</span>
                <span data-testid="state-connection-used-by">{row.usedBy.length === 0 ? 'no server' : row.usedBy.map((u) => u.name).join(', ')}</span>
                <span data-testid="state-connection-modules">{row.reachable ? versionsLine(row.modules) : 'not reachable'}</span>
                <span className="row" style={{ gap: 8 }}>
                  <button type="button" className="btn" disabled={test.isPending} onClick={() => test.mutate(row.name)} data-testid="state-connection-test">Test</button>
                  <button type="button" className="btn" onClick={() => setEditing(row)} data-testid="state-connection-edit">Edit</button>
                  <button type="button" className="btn" disabled={remove.isPending} onClick={() => remove.mutate(row.name)} data-testid="state-connection-remove">Remove</button>
                </span>
              </div>
              {tests[row.name] && (
                <div className="empty" role="status" data-testid="state-connection-test-result" data-ok={tests[row.name].ok}>
                  {tests[row.name].sentence}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
      <div style={{ marginTop: 12 }}>
        <button type="button" className="btn btn-primary" onClick={() => setEditing('new')} data-testid="state-connection-add">Add a state connection</button>
      </div>
      {editing && (
        <StateConnectionForm
          key={editing === 'new' ? 'new' : editing.name}
          existing={editing === 'new' ? null : editing}
          onDone={() => { setEditing(null); client.invalidateQueries({ queryKey: key }) }}
          onCancel={() => setEditing(null)}
        />
      )}
    </div>
  )
}

/** The connection string without its password, as the form shows it for an edit: the console keeps the password under a reference. */
const withoutPassword = (connection: string | null) => (connection ?? '').split(';').filter((part) => part.trim() !== '' && !/^\s*(password|pwd)\s*=/i.test(part)).join(';')

function StateConnectionForm({ existing, onDone, onCancel }: { existing: StateConnectionRow | null; onDone: () => void; onCancel: () => void }) {
  const [name, setName] = useState(existing?.name ?? '')
  const [engine, setEngine] = useState<string>(existing?.engine ?? 'postgres')
  const [connection, setConnection] = useState(existing?.engine === 'sqlite' ? '' : withoutPassword(existing?.connection ?? null))
  const [path, setPath] = useState(existing?.engine === 'sqlite' ? existing.connection ?? '' : '')
  const [password, setPassword] = useState('')
  const [note, setNote] = useState(existing?.note ?? '')
  const save = useMutation({
    mutationFn: () => stateApi.connections.save(name.trim(), {
      engine, connection: engine === 'sqlite' ? null : connection.trim(), path: engine === 'sqlite' ? path.trim() : null, password: password || null, note: note.trim() || null,
    }),
    onSuccess: () => { setPassword(''); onDone() },
  })
  const sqlite = engine === 'sqlite'

  return (
    <div className="card" style={{ marginTop: 16 }} data-testid="state-connection-form">
      <div className="card-head"><span className="card-title">{existing ? `Edit ${existing.name}` : 'Add a state connection'}</span></div>
      <div className="card-body">
        <ErrorBanner error={save.error} />
        <Field label="Name">
          <input className="input mono" value={name} disabled={existing !== null} onChange={(e) => setName(e.target.value)} placeholder="warehouse-state" data-testid="state-connection-name" />
        </Field>
        <Field label="Engine">
          <select className="input" value={engine} onChange={(e) => setEngine(e.target.value)} data-testid="state-connection-engine">
            <option value="postgres">PostgreSQL</option>
            <option value="mssql">SQL Server</option>
            <option value="sqlite">SQLite (a file on this console&apos;s machine)</option>
          </select>
        </Field>
        {sqlite ? (
          <Field label="File">
            <input className="input mono" value={path} onChange={(e) => setPath(e.target.value)} placeholder="/var/lib/dbdatakit/state.db" data-testid="state-connection-path" />
          </Field>
        ) : (
          <>
            <Field label="Connection string (without the password)">
              <span className="hint">Server, database and login, such as <span className="mono">Host=db.example;Port=5432;Database=dbdata_state;Username=dbdata</span>.</span>
              <input className="input mono" value={connection} onChange={(e) => setConnection(e.target.value)} data-testid="state-connection-string" />
            </Field>
            <Field label={existing ? 'Password (leave empty to keep the stored one)' : 'Password'}>
              <span className="hint">Kept in this console&apos;s secret store; the connection refers to it as a secret reference.</span>
              <input className="input" type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} data-testid="state-connection-password" />
            </Field>
          </>
        )}
        <Field label="Note (optional)">
          <input className="input" value={note} onChange={(e) => setNote(e.target.value)} data-testid="state-connection-note" />
        </Field>
        <div className="row" style={{ gap: 8 }}>
          <button type="button" className="btn btn-primary" disabled={save.isPending || !name.trim() || (sqlite ? !path.trim() : !connection.trim())} onClick={() => save.mutate()} data-testid="state-connection-save">
            {save.isPending ? 'Saving…' : 'Save'}
          </button>
          <button type="button" className="btn" onClick={onCancel} data-testid="state-connection-cancel">Cancel</button>
        </div>
      </div>
    </div>
  )
}
