import { request } from '@dbdatakit/shell'

const base = '/api/v1/identity'
const enc = encodeURIComponent
const post = <T>(path: string, body: unknown) => request<T>(`${base}/${path}`, { method: 'POST', body: JSON.stringify(body) })
const remove = (path: string) => request<void>(`${base}/${path}`, { method: 'DELETE' })

export type Person = { id: string; name: string; email: string | null; email_verified: boolean; enabled: boolean; ways_in: string[]; groups: string[] }
export type Group = { id: string; name: string; members: string[] }
export type Assignment = { id: string; subject_kind: 'user' | 'group'; subject: string; subject_name: string; scope: 'server' | 'project' | 'app'; target: string; project: string | null; roles: string[] }
export type Client = { id: string; name: string; redirect_uris: string[]; logout_redirect_uris: string[]; confidential: boolean }
export type GroupMapping = { id: string; source: 'entra' | 'windows'; group: string; scope: 'server' | 'project' | 'app'; target: string; project: string | null; roles: string[] }
export type AttributeMapping = { id: string; source: 'entra' | 'windows'; group: string; attribute: string; value: string }
export type Token = { id: string; user: string; user_name: string | null; name: string; created: string; expires: string | null; revoked: string | null; last_used: string | null; active: boolean }
export type ServiceAccount = { id: string; name: string; description: string | null; created_by: string; active_tokens: number }

/** Where an assignment or a mapping applies: a server (with a Build project) or an app. */
export type Place = { server?: string; project?: string; app?: string }

/** The identity home's admin API (plan 13a U21) on the management API of the server that hosts it, through the console's relay. */
export const identityApi = {
  people: {
    list: () => request<{ people: Person[] }>(`${base}/people`).then((r) => r.people),
    invite: (email: string, role: 'admin' | 'viewer') => post<{ url: string; expires: string }>('people', { email, role }),
    disable: (id: string) => remove(`people/${enc(id)}`),
    enable: (id: string) => post<void>(`people/${enc(id)}/enable`, {}),
  },
  groups: {
    list: () => request<{ groups: Group[] }>(`${base}/groups`).then((r) => r.groups),
    create: (name: string) => post<Group>('groups', { name }),
    remove: (id: string) => remove(`groups/${enc(id)}`),
    addMember: (id: string, user: string) => post<void>(`groups/${enc(id)}/members`, { user }),
    removeMember: (id: string, user: string) => remove(`groups/${enc(id)}/members/${enc(user)}`),
  },
  assignments: {
    list: () => request<{ assignments: Assignment[] }>(`${base}/assignments`).then((r) => r.assignments),
    add: (who: { user?: string; group?: string }, place: Place, roles: string[]) => post<unknown>('assignments', { ...who, ...place, roles }),
    remove: (id: string) => remove(`assignments/${enc(id)}`),
  },
  clients: {
    list: () => request<{ clients: Client[] }>(`${base}/clients`).then((r) => r.clients),
    remove: (id: string) => remove(`clients/${enc(id)}`),
  },
  mappings: {
    list: () => request<{ group_mappings: GroupMapping[] }>(`${base}/group-mappings`).then((r) => r.group_mappings),
    add: (source: string, group: string, place: Place, roles: string[]) => post<unknown>('group-mappings', { source, group, ...place, roles }),
    remove: (id: string) => remove(`group-mappings/${enc(id)}`),
  },
  /** A directory group gives its members an attribute value (plan 22a GP04); the values reach a token as the `attrs` claim and decide what a person sees. */
  attributeMappings: {
    list: () => request<{ attribute_mappings: AttributeMapping[] }>(`${base}/attribute-mappings`).then((r) => r.attribute_mappings),
    add: (m: { source: string; group: string; attribute: string; value: string }) => post<{ attribute_mappings: AttributeMapping[] }>('attribute-mappings', m),
    remove: (id: string) => remove(`attribute-mappings/${enc(id)}`),
  },
  tokens: {
    list: () => request<{ tokens: Token[] }>(`${base}/tokens`).then((r) => r.tokens),
    revoke: (id: string) => remove(`tokens/${enc(id)}`),
  },
  serviceAccounts: {
    list: () => request<{ service_accounts: ServiceAccount[] }>(`${base}/service-accounts`).then((r) => r.service_accounts),
    create: (name: string, description: string) => post<{ id: string; name: string }>('service-accounts', { name, description }),
    remove: (name: string) => remove(`service-accounts/${enc(name)}`),
  },
}
