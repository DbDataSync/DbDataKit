import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { parseYaml } from '@dbdatakit/shell'
import { editorKindOf } from '../editors'
import { connectionsOf, newConnectionText, newReplicationFiles, replicationsOf, secretFindings, syncNameProblem, connectionPath, replicationPath, withCurrentTaskVersion } from '../syncSchemas'
import type { LiveCheck } from '../useLiveCheck'
import { SecretNotes, SyncFileEditor, SyncSetPanel } from './SyncSetPanel'

const idle: LiveCheck = { diagnostics: [], checkedText: null, running: false, failed: null }
const paths = [
  'config/connections/source-db.yaml', 'config/connections/target-db.yaml',
  'config/replications/orders/task.yaml', 'config/replications/orders/table-mappings/orders.yaml',
  'config/replications/stock/task.yaml', 'README.md',
]

describe('which editor a file of a Sync project gets', () => {
  it('tells task, mapping and connection by where Sync keeps them', () => {
    expect(editorKindOf('sync', 'config/replications/orders/task.yaml')).toBe('sync-task')
    expect(editorKindOf('sync', 'config/replications/orders/table-mappings/orders.yaml')).toBe('sync-mapping')
    expect(editorKindOf('sync', 'config/connections/source-db.yaml')).toBe('sync-connection')
    expect(editorKindOf('sync', 'config/scripts/x.yaml')).toBeNull()
    expect(editorKindOf('sync', 'README.md')).toBeNull()
    expect(editorKindOf('store', 'config/connections/source-db.yaml')).toBeNull()
  })
})

describe('the set of a Sync project', () => {
  it('lists the replications and connections from the files', () => {
    expect(replicationsOf(paths)).toEqual(['orders', 'stock'])
    expect(connectionsOf(paths)).toEqual(['source-db', 'target-db'])
  })
  it('refuses a name that is taken or malformed', () => {
    expect(syncNameProblem('', paths, replicationPath)).toBeNull()
    expect(syncNameProblem('orders', paths, replicationPath)).toContain('already exists')
    expect(syncNameProblem('new one', paths, replicationPath)).toContain('Letters')
    expect(syncNameProblem('source-db', paths, connectionPath)).toContain('already exists')
    expect(syncNameProblem('billing', paths, replicationPath)).toBeNull()
  })
  it('makes a new replication as a task and a mapping naming its two connections, both readable as YAML in the current format', () => {
    const [task, mapping] = newReplicationFiles('billing', 'source-db', 'target-db')
    expect(task.path).toBe('config/replications/billing/task.yaml')
    expect(mapping.path).toBe('config/replications/billing/table-mappings/billing.yaml')
    const t = parseYaml(task.text)
    expect(t.errors).toEqual([])
    expect((t.value as { schemaVersion: number; endpoints: { source: { connectionName: string } } }).schemaVersion).toBe(2)
    expect((t.value as { endpoints: { source: { connectionName: string }; target: { connectionName: string } } }).endpoints.target.connectionName).toBe('target-db')
    expect(parseYaml(mapping.text).errors).toEqual([])
    expect(mapping.text).toContain('sourceColumns:')
  })
  it('draws the replications with their mappings, the connections, and the forms only for an author', () => {
    const html = renderToStaticMarkup(<SyncSetPanel paths={paths} canWrite busy={false} onOpen={() => {}} onCreate={() => {}} />)
    expect(html.match(/data-testid="wsp-sync-replication"/g)).toHaveLength(2)
    expect(html.match(/data-testid="wsp-sync-connection"/g)).toHaveLength(2)
    expect(html).toContain('data-testid="wsp-sync-open-mapping"')
    expect(html).toContain('data-testid="wsp-new-replication"')
    const reader = renderToStaticMarkup(<SyncSetPanel paths={paths} canWrite={false} busy={false} onOpen={() => {}} onCreate={() => {}} />)
    expect(reader).not.toContain('wsp-new-replication')
    expect(reader).not.toContain('wsp-new-connection')
  })
  it('counts the errors of each replication from the findings', () => {
    const html = renderToStaticMarkup(<SyncSetPanel paths={paths} canWrite busy={false} onOpen={() => {}} onCreate={() => {}}
      findings={[{ severity: 'error', code: 'DBD-WS-SYNC', message: 'bad schedule', file: 'config/replications/orders/task.yaml', line: 4 }]} />)
    expect(html).toMatch(/data-name="orders" data-errors="1"/)
    expect(html).toMatch(/data-name="stock" data-errors="0"/)
    expect(html).toContain('1 to fix')
  })
})

describe('a connection holds a reference, never a password', () => {
  it('finds each of the three ways a secret reaches the file', () => {
    expect(secretFindings(newConnectionText('source-db', 'sqlserver'))).toEqual([])
    expect(secretFindings('auth:\n  password: hunter2\n')).toEqual([{ line: 2, message: expect.stringContaining('password value') }])
    expect(secretFindings('auth:\n  password_secret: hunter2\n')).toEqual([{ line: 2, message: expect.stringContaining('not a reference') }])
    expect(secretFindings('connection_string: Server=x;Password=abc;')).toEqual([{ line: 1, message: expect.stringContaining('connection string') }])
    expect(secretFindings('connection_string: Server=x;Password=secret:dbdata:a;')).toEqual([])
    expect(secretFindings('# password: nope\n')).toEqual([])
  })
  it('shows the finding with its line under the editor, and the rule beside it', () => {
    const html = renderToStaticMarkup(<SecretNotes text={'name: x\nauth:\n  password: hunter2\n'} />)
    expect(html).toContain('data-problems="1"')
    expect(html).toContain('Line 3')
    expect(html).not.toContain('hunter2')
    expect(renderToStaticMarkup(<SecretNotes text={newConnectionText('a', 'postgres')} />)).toContain('data-problems="0"')
  })
  it('draws the connection form with the secret reference under it', () => {
    const text = newConnectionText('source-db', 'sqlserver')
    const html = renderToStaticMarkup(<SyncFileEditor kind="sync-connection" path="config/connections/source-db.yaml" text={text} onText={() => {}} readOnly={false} live={idle} />)
    expect(html).toContain('data-testid="wsp-form"')
    expect(html).toContain('wsp-secret-notes')
    expect(html).toContain('secret:dbdata:sync:connection:source-db')
  })
})

describe('the editor writes the current task format', () => {
  const v1 = 'schemaVersion: 1\nname: orders\nscheduling:\n  mode: Periodic\n  cronExpression: "*/15 * * * *"\n'
  it('raises a file read as 1 to 2 and changes nothing else', () => {
    const out = withCurrentTaskVersion(v1)
    expect(out).toContain('schemaVersion: 2')
    expect(out).toContain('name: orders')
    expect(out).toContain('cronExpression')
  })
  it('leaves a current file, text that is not YAML and a file with no version as they are', () => {
    expect(withCurrentTaskVersion('schemaVersion: 2\nname: x\n')).toBe('schemaVersion: 2\nname: x\n')
    expect(withCurrentTaskVersion('name: [')).toBe('name: [')
    expect(withCurrentTaskVersion('name: x\n')).toBe('name: x\n')
  })
  it('draws the task form from the replication schema', () => {
    const html = renderToStaticMarkup(<SyncFileEditor kind="sync-task" path="config/replications/orders/task.yaml" text={v1} onText={() => {}} readOnly={false} live={idle} />)
    expect(html).toContain('data-testid="form-field-name"')
    expect(html).toContain('data-testid="form-field-scheduling.cronExpression"')
  })
})
