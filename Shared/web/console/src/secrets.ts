import { request } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'

/** What `GET /api/v1/secrets/overview` answers on a Gateway and on a runner (`SecretsOverview`): names, references and dates, never a value. */
export interface SecretsOverview {
  product: string
  provider: string
  secrets: { name: string; reference: string; updated: string | null; used_by: string[] }[]
  references: { reference: string; kind: string; status: 'present' | 'missing' | 'outside'; used_by: string[] }[]
  missing: string[]
  unused: string[]
}

/** One server's overview, or why there is none. */
export interface ServerSecrets {
  server: string
  serverName: string
  product: string
  /** Null when the server has no secrets route (an older version, the console's own embedded runner) or did not answer; `problem` says which. */
  overview: SecretsOverview | null
  problem: string | null
}

/** A line of the page: a reference nothing answers, a secret nothing names, or a reference outside the server's store. */
export interface SecretFinding {
  key: string
  server: string
  serverName: string
  kind: 'missing' | 'unused' | 'outside'
  /** The reference (missing, outside) or the secret's name (unused). */
  subject: string
  /** What names it (missing, outside), or when the secret was last written (unused). */
  detail: string
  link: string
}

/** The products that keep secrets and answer the overview. */
export const HOLDS_SECRETS = ['dbdatagateway', 'dbdatarunner']

const where = (server: Pick<ServerSecrets, 'server' | 'product'>) => (server.product === 'dbdatagateway' ? `/servers/${server.server}/secrets` : `/servers/${server.server}/packages`)

/** The findings of every server's overview, the ones that need somebody first: missing, then unused, then outside, server by server. */
export function findings(all: ServerSecrets[]): SecretFinding[] {
  const rows: SecretFinding[] = []
  for (const s of all) {
    if (!s.overview) continue
    const base = { server: s.server, serverName: s.serverName, link: where(s) }
    for (const r of s.overview.references) {
      if (r.status === 'missing') rows.push({ ...base, key: `${s.server}/missing/${r.reference}`, kind: 'missing', subject: r.reference, detail: `named by ${r.used_by.join(', ')}` })
      if (r.status === 'outside') rows.push({ ...base, key: `${s.server}/outside/${r.reference}`, kind: 'outside', subject: r.reference, detail: `named by ${r.used_by.join(', ')}; this server cannot say whether it answers` })
    }
    for (const name of s.overview.unused) {
      const secret = s.overview.secrets.find((x) => x.name === name)
      rows.push({ ...base, key: `${s.server}/unused/${name}`, kind: 'unused', subject: name, detail: secret?.updated ? `written ${new Date(secret.updated).toLocaleDateString()}` : 'no definition names it' })
    }
  }
  const order = { missing: 0, unused: 1, outside: 2 } as const
  return rows.sort((a, b) => order[a.kind] - order[b.kind] || a.serverName.localeCompare(b.serverName) || a.subject.localeCompare(b.subject))
}

/** How many of each kind, for the sentence above the list. */
export const counts = (rows: SecretFinding[]) => ({ missing: rows.filter((r) => r.kind === 'missing').length, unused: rows.filter((r) => r.kind === 'unused').length, outside: rows.filter((r) => r.kind === 'outside').length })

/** The overview of every reachable Gateway and runner. A server that does not have the route, or does not answer, is named beside the findings and never stops the others. */
export async function loadSecrets(servers: InstanceSummary[]): Promise<ServerSecrets[]> {
  return Promise.all(servers.filter((s) => s.reachable && s.product !== null && HOLDS_SECRETS.includes(s.product)).map(async (s): Promise<ServerSecrets> => {
    const base = { server: s.id, serverName: s.name, product: s.product! }
    try {
      return { ...base, overview: await request<SecretsOverview>(`/relay/${s.id}/api/v1/secrets/overview`), problem: null }
    } catch (error) {
      return { ...base, overview: null, problem: error instanceof Error ? error.message : 'No answer' }
    }
  }))
}
