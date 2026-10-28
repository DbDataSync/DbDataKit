import { execFileSync } from 'node:child_process'

export const DB_NAME = 'DbDataSyncPlaywrightTest'
export const SOURCE_TABLE = 'PwSrcItems'
export const TARGET_TABLE = 'PwTgtItems'
export const SRC_CONNECTION_NAME = 'playwright-src'
export const TGT_CONNECTION_NAME = 'playwright-tgt'

/**
 * The test engines are the shared ones in testenv/ (`testenv/testenv.sh up mssql postgres-logical mysql`, then
 * `eval "$(testenv/testenv.sh env dbdatasync)"`), whose connection strings arrive in DBDATASYNC_TEST_*. The defaults are what the old
 * DbDataSync/docker-compose.yml had (password DbDataSync_Test_Pw1, SQL Server on 14330, MySQL on 13306), for a run without the variables.
 */
function part(connectionString: string, keys: string[]): string | undefined {
  for (const pair of connectionString.split(';')) {
    const at = pair.indexOf('=')
    if (at > 0 && keys.includes(pair.slice(0, at).trim().toLowerCase())) return pair.slice(at + 1).trim()
  }
  return undefined
}

function portOf(connectionString: string, fallback: number): number {
  const address = part(connectionString, ['data source', 'server', 'host']) ?? ''
  const comma = address.indexOf(',')
  const text = comma >= 0 ? address.slice(comma + 1) : part(connectionString, ['port'])
  return Number(text) || fallback
}

const MSSQL = process.env.DBDATASYNC_TEST_MSSQL_SERVER
  ?? 'Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True'
const MYSQL = process.env.DBDATASYNC_TEST_MYSQL_SERVER
  ?? 'Server=localhost;Port=13306;UserID=root;Password=DbDataSync_Test_Pw1'

export const SA_PASSWORD = part(MSSQL, ['password', 'pwd']) ?? 'DbDataSync_Test_Pw1'
export const MSSQL_PORT = portOf(MSSQL, 14330)
export const MYSQL_ROOT_PASSWORD = part(MYSQL, ['password', 'pwd']) ?? 'DbDataSync_Test_Pw1'
export const MYSQL_PORT = portOf(MYSQL, 13306)

/** The container `docker exec` runs sqlcmd in: the shared testenv's SQL Server. */
const MSSQL_CONTAINER = 'dbkit-test-mssql'

/**
 * Where sqlcmd lives in the running container, and whether it needs `-C`.
 *
 * The path moved: images from 2025 onward ship `/opt/mssql-tools18` only, and that sqlcmd defaults to
 * encrypted connections, so it fails against the container's self-signed certificate without `-C`.
 * `:2022-latest` moves, so a machine can be holding either generation — resolved once by asking the
 * container rather than assuming.
 */
let sqlcmd: string[] | undefined

function sqlcmdArgs(): string[] {
  if (sqlcmd) return sqlcmd
  try {
    execFileSync('docker', ['exec', MSSQL_CONTAINER, 'test', '-x', '/opt/mssql-tools18/bin/sqlcmd'])
    sqlcmd = ['/opt/mssql-tools18/bin/sqlcmd', '-C']
  } catch {
    sqlcmd = ['/opt/mssql-tools/bin/sqlcmd']
  }
  return sqlcmd
}

export function runSql(sql: string, database?: string): void {
  const args = ['exec', MSSQL_CONTAINER, ...sqlcmdArgs(), '-S', 'localhost', '-U', 'sa', '-P', SA_PASSWORD]
  if (database) args.push('-d', database)
  args.push('-Q', sql)
  execFileSync('docker', args, { stdio: 'inherit' })
}

/**
 * Same as runSql, but captures and returns stdout instead of inheriting it — for assertions.
 * Passes `-h -1` (no column headers) so callers get just data rows back, not sqlcmd's header +
 * separator lines mixed in with them.
 */
export function querySql(sql: string, database?: string): string {
  const args = ['exec', MSSQL_CONTAINER, ...sqlcmdArgs(), '-S', 'localhost', '-U', 'sa', '-P', SA_PASSWORD, '-h', '-1']
  if (database) args.push('-d', database)
  args.push('-Q', sql)
  return execFileSync('docker', args, { encoding: 'utf-8' })
}
