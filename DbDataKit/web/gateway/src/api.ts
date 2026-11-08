import { put, request } from '@dbdatakit/shell'
import type { InstanceRequest } from './appEditors'
import type {
  AdoptAnswer, AppApprovals, AppDetail, AppInstances, AppsList, NavigationSetRow, ServerClient, CertificateInfo, JwtMapping, CommitDiff, CommitRow, DataSourceFile, DataSourceSummary, FailureRow, Identities, Limits, NotificationRow, Overview, QueryRow, SessionRow, TestResult, WriteDataSourceResult,
} from './types'

const v1 = '/api/v1'
const enc = encodeURIComponent

/** The calls of the Gateway's management API, through the console's relay. A write names the version it read; the server refuses one that is stale with the current version. */
export const gatewayApi = {
  overview: () => request<Overview>(`${v1}/overview`),
  dataSources: {
    list: () => request<{ data_sources: DataSourceSummary[] }>(`${v1}/data-sources`).then((r) => r.data_sources),
    read: (name: string) => request<DataSourceFile>(`${v1}/data-sources/${enc(name)}`),
    write: (name: string, text: string, expectedVersion: string | null) => put<WriteDataSourceResult>(`${v1}/data-sources/${enc(name)}`, { text, expected_version: expectedVersion }),
    remove: (name: string, expectedVersion: string) => request<void>(`${v1}/data-sources/${enc(name)}?expected_version=${enc(expectedVersion)}`, { method: 'DELETE' }),
    test: (name: string) => request<TestResult>(`${v1}/data-sources/${enc(name)}/test`, { method: 'POST' }),
    /** A person with the admin role hands a data source to a paired server client, which then manages it (T12). */
    adopt: (name: string, client: string) => request<AdoptAnswer>(`${v1}/data-sources/${enc(name)}/adopt`, { method: 'POST', body: JSON.stringify({ client }) }),
  },
  /** The paired server clients a data source can be handed to. */
  clients: () => request<{ clients: ServerClient[] }>(`${v1}/clients`).then((r) => r.clients),
  identities: {
    get: () => request<Identities>(`${v1}/identities`),
    writeUser: (name: string, body: { auth_type: string; password?: string; data_sources: string[]; default_data_source?: string | null; expected_version?: string | null }) =>
      put<{ name: string; version: string }>(`${v1}/users/${enc(name)}`, body),
    removeUser: (name: string, expectedVersion: string) => request<void>(`${v1}/users/${enc(name)}?expected_version=${enc(expectedVersion)}`, { method: 'DELETE' }),
    setJwtMappings: (mappings: JwtMapping[], expectedVersion: string | null) => put<{ jwt_mappings: number }>(`${v1}/auth`, { jwt_mappings: mappings, expected_version: expectedVersion }),
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
  /** Apps (plan 26, AP24): reads are for a viewer; what changes something needs the role the Gateway names in its refusal, which the screen shows. */
  apps: {
    list: () => request<AppsList>(`${v1}/apps`),
    detail: (name: string) => request<AppDetail>(`${v1}/apps/${enc(name)}`),
    instances: () => request<AppInstances>(`${v1}/app-instances`),
    navigation: () => request<{ navigation: NavigationSetRow[] }>(`${v1}/app-navigation`).then((r) => r.navigation),
    approvals: () => request<AppApprovals>(`${v1}/app-approvals`),
    /** Moves what an app serves (an unpinned instance follows); no version means the one before. */
    rollback: (app: string, to?: number) => request<{ name: string; version: number }>(`${v1}/apps/${enc(app)}/rollback`, { method: 'POST', body: JSON.stringify(to === undefined ? {} : { to }) }),
    /** Makes or changes an instance. The Gateway replaces the whole instance, so a call that changes one thing (the pin) sends the rest back unchanged (`pinRequest`). */
    saveInstance: (name: string, body: InstanceRequest) => put<{ name: string; app: string; version: number }>(`${v1}/app-instances/${enc(name)}`, body),
    removeInstance: (name: string) => request<{ name: string; removed: boolean }>(`${v1}/app-instances/${enc(name)}`, { method: 'DELETE' }),
    removeApp: (name: string) => request<{ name: string; removed: boolean }>(`${v1}/apps/${enc(name)}`, { method: 'DELETE' }),
    approve: (app: string, version: number, by: string, note: string) =>
      request<{ id: string }>(`${v1}/app-approvals`, { method: 'POST', body: JSON.stringify({ app, version, by, note: note || undefined }) }),
    revoke: (id: string, by: string) => request<{ id: string }>(`${v1}/app-approvals/${enc(id)}/revoke`, { method: 'POST', body: JSON.stringify({ by }) }),
  },
  history: {
    list: () => request<{ commits: CommitRow[] }>(`${v1}/config/history`).then((r) => r.commits),
    commit: (sha: string) => request<CommitDiff>(`${v1}/config/history/${enc(sha)}`),
  },
}
