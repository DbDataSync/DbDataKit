/** The engines the tool builds on, which can hold models, and a DuckDB file as a read-only origin. (ODBC and Flight SQL origins are written in the file by hand.) */
export const CONNECTION_ENGINES: { key: string; label: string; origin?: boolean }[] = [
  { key: 'sqlserver', label: 'SQL Server' },
  { key: 'postgres', label: 'PostgreSQL' },
  { key: 'fabric', label: 'Microsoft Fabric' },
  // a file the tool only reads, to import tables from or copy from: no address (the login for it is the file’s path), and never written to
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

export const connectionPath = (form: ConnectionForm) => `connections/${form.name}.yaml`

/**
 * The `connection/1` file for the form (one file per connection, named like it). A Build connection file names the engine and how the connection is secured and nothing else: where the database is and who logs in are bound
 * where the work runs (the Runner's `build-login` slot of the package), so the file can be committed and reviewed.
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
