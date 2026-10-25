import { put, request } from '@dbdatakit/shell'
import type {
  CertificateInfo, CommitDiff, CommitRow, DataSourceFile, DataSourceSummary, FailureRow, Identities, Limits, NotificationRow, Overview, QueryRow, SessionRow, TestResult, WriteDataSourceResult,
} from './types'

const v1 = '/api/v1'
const enc = encodeURIComponent

/** The calls of the Proxy's management API, through the console's relay. A write names the version it read; the server refuses one that is stale with the current version. */
export const proxyApi = {
  overview: () => request<Overview>(`${v1}/overview`),
  dataSources: {
    list: () => request<{ data_sources: DataSourceSummary[] }>(`${v1}/data-sources`).then((r) => r.data_sources),
    read: (name: string) => request<DataSourceFile>(`${v1}/data-sources/${enc(name)}`),
    write: (name: string, text: string, expectedVersion: string | null) => put<WriteDataSourceResult>(`${v1}/data-sources/${enc(name)}`, { text, expected_version: expectedVersion }),
    remove: (name: string, expectedVersion: string) => request<void>(`${v1}/data-sources/${enc(name)}?expected_version=${enc(expectedVersion)}`, { method: 'DELETE' }),
    test: (name: string) => request<TestResult>(`${v1}/data-sources/${enc(name)}/test`, { method: 'POST' }),
  },
  identities: {
    get: () => request<Identities>(`${v1}/identities`),
    writeUser: (name: string, body: { auth_type: string; password?: string; data_sources: string[]; default_data_source?: string | null; expected_version?: string | null }) =>
      put<{ name: string; version: string }>(`${v1}/users/${enc(name)}`, body),
    removeUser: (name: string, expectedVersion: string) => request<void>(`${v1}/users/${enc(name)}?expected_version=${enc(expectedVersion)}`, { method: 'DELETE' }),
    setPassThrough: (allow: boolean, expectedVersion: string | null) => put<{ allow_pass_through: boolean }>(`${v1}/auth`, { allow_pass_through: allow, expected_version: expectedVersion }),
  },
  certificate: {
    get: () => request<CertificateInfo>(`${v1}/certificate`),
    renew: () => request<CertificateInfo>(`${v1}/certificate/renew`, { method: 'POST', body: JSON.stringify({ confirm: true }) }),
  },
  activity: {
    sessions: () => request<{ sessions: SessionRow[] }>(`${v1}/sessions`).then((r) => r.sessions),
    endSession: (id: string) => request<void>(`${v1}/sessions/${enc(id)}`, { method: 'DELETE' }),
    queries: () => request<{ queries: QueryRow[] }>(`${v1}/queries`).then((r) => r.queries),
    cancelQuery: (id: string) => request<void>(`${v1}/queries/${enc(id)}/cancel`, { method: 'POST' }),
    limits: () => request<Limits>(`${v1}/limits`),
    liftLockout: (client: string, user: string) => request<void>(`${v1}/throttle?client=${enc(client)}&user=${enc(user)}`, { method: 'DELETE' }),
    failures: () => request<{ failures: FailureRow[] }>(`${v1}/failures`).then((r) => r.failures),
    notifications: () => request<{ notifications: NotificationRow[] }>(`${v1}/notifications`).then((r) => r.notifications),
  },
  history: {
    list: () => request<{ commits: CommitRow[] }>(`${v1}/config/history`).then((r) => r.commits),
    commit: (sha: string) => request<CommitDiff>(`${v1}/config/history/${enc(sha)}`),
  },
}
