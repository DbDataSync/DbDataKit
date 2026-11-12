import { put, request } from '@dbdatakit/shell'
import type { RenderedViewDocument } from '@dbdatakit/visuals'
import type { InstanceRequest, NavigationRequest, PublishBody } from './appEditors'
import type { CacheOverview } from './cacheTypes'
import type { ReplayResult, RunDetail, RunResult, RunRow, SavedQueryRow, SavedQueryVersionRow } from './runTypes'
import type {
  AdoptAnswer, PruneAnswer, SemanticCatalogView, EntityModelDetail, EntityModelRow, SemanticModelDetail, SemanticModelRow, AppApprovals, AppCheck, AppDetail, AppInstances, AppsList, NavigationSetRow, ServerClient, CertificateInfo, JwtMapping, CommitDiff, CommitRow, DataSourceFile, DataSourceSummary, FailureRow, Identities, Limits, NotificationRow, Overview, QueryRow, SecretWritten, SecretsList, ServerSettings, SettingsCheck, SettingsEdit, SettingsWritten, SessionRow, TestResult, WriteDataSourceResult,
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
  /** Prune the versions of a definition of any serving kind (plan 30 PB05); `dry_run` true says what would go and removes nothing. An administrator's call. */
  definitions: {
    prune: (kind: string, name: string, body: { versions: number[]; dry_run: boolean }) => request<PruneAnswer>(`${v1}/definitions/${enc(kind)}/${enc(name)}/prune`, { method: 'POST', body: JSON.stringify(body) }),
  },
  /** The paired server clients a data source can be handed to. */
  clients: () => request<{ clients: ServerClient[] }>(`${v1}/clients`).then((r) => r.clients),
  /** The models the group serves (plan 33 UE12): semantic models (versions, serve an earlier one, remove) and the entity models a DbDataStore publishes (versions, remove). The calls are an administrator's or a publisher's. */
  models: {
    semantic: {
      list: () => request<{ models: SemanticModelRow[] }>(`${v1}/semantic-models`).then((r) => r.models),
      read: (name: string) => request<SemanticModelDetail>(`${v1}/semantic-models/${enc(name)}`),
      /** What the catalog says, for anyone who may query the model (viewer role; plan 14a SL12). */
      catalog: (name: string) => request<SemanticCatalogView>(`${v1}/semantic-models/${enc(name)}/catalog`),
      /** Serves an earlier version again (every version is kept); no version means the one before the current. */
      rollback: (name: string, to?: number) => request<{ name: string; version: number; from_version: number }>(`${v1}/semantic-models/${enc(name)}/rollback`, { method: 'POST', body: JSON.stringify(to === undefined ? {} : { to_version: to }) }),
      remove: (name: string) => request<{ name: string; removed: boolean }>(`${v1}/semantic-models/${enc(name)}`, { method: 'DELETE' }),
    },
    entity: {
      list: () => request<{ models: EntityModelRow[] }>(`${v1}/entity-models`).then((r) => r.models),
      read: (name: string) => request<EntityModelDetail>(`${v1}/entity-models/${enc(name)}`),
      remove: (name: string) => request<{ name: string; removed?: boolean }>(`${v1}/entity-models/${enc(name)}`, { method: 'DELETE' }),
    },
  },
  /** A person's statements on the models (plan 14a SL14, SL15, SL23, SL24): run one as the person (the explorer's call: one function, so the REST listener of SL31 can replace it), the record of what they ran, replay one. */
  runs: {
    run: (body: Record<string, unknown>) => request<RunResult>(`${v1}/my/runs`, { method: 'POST', body: JSON.stringify(body) }),
    /** Draws a kept result as a view document (a matrix or a chart) through the Gateway's renderer, running nothing; a 410 says the result is gone and the statement must be run again (with the `view`). */
    render: (resultId: string, view: unknown) => request<{ result_id: string; rendered_view: RenderedViewDocument }>(`${v1}/my/runs/${enc(resultId)}/render`, { method: 'POST', body: JSON.stringify({ view }) }),
    list: (opts: { model?: string; all?: boolean; limit?: number } = {}) => {
      const q = new URLSearchParams()
      if (opts.model) q.set('model', opts.model)
      if (opts.all) q.set('all', 'true')
      if (opts.limit) q.set('limit', String(opts.limit))
      const text = q.toString()
      return request<{ runs: RunRow[]; all: boolean; retention_days: number }>(`${v1}/my/runs${text ? `?${text}` : ''}`)
    },
    read: (id: number) => request<RunDetail>(`${v1}/my/runs/${id}`),
    /** Runs a record's request again as the caller (D38 (3)): `keep` and `view` as a run takes them, so a replay is also how a record is opened in the explorer; the view is drawn and never stored in the record. */
    replay: (id: number, body?: { keep?: boolean; view?: unknown }) => request<ReplayResult>(`${v1}/my/runs/${id}/replay`, { method: 'POST', ...(body ? { body: JSON.stringify(body) } : {}) }),
    counts: (days = 7) => request<{ days: number; by_code: Record<string, number> }>(`${v1}/my/runs/counts?days=${days}`),
  },
  /** A person's own saved queries and what is shared with them (SL17, SL18). The model is in the path because a saved query is a model's. */
  savedQueries: {
    list: (model?: string) => request<{ queries: SavedQueryRow[] }>(`${v1}/my/queries${model ? `?model=${enc(model)}` : ''}`).then((r) => r.queries),
    read: (model: string, name: string, owner?: string) => request<SavedQueryRow>(`${v1}/my/queries/${enc(model)}/${enc(name)}${owner ? `?owner=${enc(owner)}` : ''}`),
    save: (model: string, name: string, body: { sql?: string; request?: Record<string, unknown>; description?: string | null; parameters?: Record<string, unknown>; expected_version?: number }) =>
      put<SavedQueryRow>(`${v1}/my/queries/${enc(model)}/${enc(name)}`, body),
    remove: (model: string, name: string) => request<{ name: string; removed: boolean }>(`${v1}/my/queries/${enc(model)}/${enc(name)}`, { method: 'DELETE' }),
    history: (model: string, name: string) => request<{ versions: SavedQueryVersionRow[] }>(`${v1}/my/queries/${enc(model)}/${enc(name)}/history`).then((r) => r.versions),
    restore: (model: string, name: string, version: number) => request<SavedQueryRow>(`${v1}/my/queries/${enc(model)}/${enc(name)}/restore`, { method: 'POST', body: JSON.stringify({ version }) }),
    shares: (model: string, name: string) => request<{ grantees: string[] }>(`${v1}/my/queries/${enc(model)}/${enc(name)}/shares`).then((r) => r.grantees),
    setShares: (model: string, name: string, grantees: string[]) => put<{ grantees: string[] }>(`${v1}/my/queries/${enc(model)}/${enc(name)}/shares`, { grantees }),
  },
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
    /** Approves one published version, for the outside origins chosen (`connect=https://api.x.com,img=...`: only origins the manifest asks for; none by default). */
    approve: (app: string, version: number, by: string, note: string, egress?: string) =>
      request<{ id: string }>(`${v1}/app-approvals`, { method: 'POST', body: JSON.stringify({ app, version, by, note: note || undefined, egress }) }),
    /** Approves exact files that are not published yet (a trusted app is published only with such an approval): the body of the publish, the approver, a note and the origins chosen. */
    approvePackage: (body: PublishBody, by: string, note: string, egress?: string) =>
      request<{ id: string; app: string; hash: string }>(`${v1}/app-approvals`, { method: 'POST', body: JSON.stringify({ manifest: body.manifest, files: body.files, by, note: note || undefined, egress }) }),
    /** What publishing these files would meet, without publishing: the Gateway's own diagnostics, the version it would be, the trust and the hash, whether an approval stands, what the manifest declares. */
    check: (body: PublishBody) => request<AppCheck>(`${v1}/apps/check`, { method: 'POST', body: JSON.stringify({ manifest: body.manifest, files: body.files }) }),
    /** Publishes a version: the manifest as text, every other file as base64 (what `apps publish --to` sends). */
    publish: (name: string, body: PublishBody) => put<{ name: string; version: number; files: number; published_by: string }>(`${v1}/apps/${enc(name)}`, body),
    /** Makes, changes or (with `rename_from`) renames a navigation set; the store refuses an unknown or repeated member in its words. */
    saveNavigation: (name: string, body: NavigationRequest) => put<{ name: string; version: number; entries: number; renamed_from: string | null }>(`${v1}/app-navigation/${enc(name)}`, body),
    removeNavigation: (name: string) => request<{ name: string; removed: boolean }>(`${v1}/app-navigation/${enc(name)}`, { method: 'DELETE' }),
    revoke: (id: string, by: string) => request<{ id: string }>(`${v1}/app-approvals/${enc(id)}/revoke`, { method: 'POST', body: JSON.stringify({ by }) }),
  },
  /** Secrets (plan 33, UE16): names and references are listed; a value is only ever sent, with the one `PUT`, and never comes back. */
  secrets: {
    list: () => request<SecretsList>(`${v1}/secrets`).then((r) => r.items ?? r.secrets.map((name) => ({ name, reference: '' }))),
    set: (name: string, value: string) => put<SecretWritten>(`${v1}/secrets/${enc(name)}`, { value }),
    remove: (name: string) => request<{ name: string; removed: boolean }>(`${v1}/secrets/${enc(name)}`, { method: 'DELETE' }),
  },
  /** server.yaml (plan 33, UE17): read with its secrets masked, checked and edited as a draft (nothing written), written against the version read. */
  settings: {
    read: () => request<ServerSettings>(`${v1}/config/server.yaml`),
    check: (text: string) => request<SettingsCheck>(`${v1}/config/server.yaml/check`, { method: 'POST', body: JSON.stringify({ text }) }),
    edit: (text: string, change: SettingsEdit) => request<{ text: string }>(`${v1}/config/server.yaml/edit`, { method: 'POST', body: JSON.stringify({ text, ...change }) }).then((r) => r.text),
    write: (text: string, expectedVersion: string | null, note: string) => put<SettingsWritten>(`${v1}/config/server.yaml`, { text, expected_version: expectedVersion, note: note || undefined }),
  },
  /** The result cache of this instance (plan 24a UC16): read for a viewer; a clear (one dataset, or all) is an administrator's. */
  cache: {
    read: () => request<CacheOverview>(`${v1}/cache`),
    clear: (body: { dataset: string } | { all: true }) => request<{ cleared: number }>(`${v1}/cache/invalidate`, { method: 'POST', body: JSON.stringify(body) }),
  },
  history: {
    list: () => request<{ commits: CommitRow[] }>(`${v1}/config/history`).then((r) => r.commits),
    commit: (sha: string) => request<CommitDiff>(`${v1}/config/history/${enc(sha)}`),
  },
}
