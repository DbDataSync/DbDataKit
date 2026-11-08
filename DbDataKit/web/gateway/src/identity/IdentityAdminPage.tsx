import { useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin, type Crumb } from '@dbdatakit/shell'
import { identityApi, type Place } from './api'

/** The frame a host draws around the page: the Gateway's rail, or the identity home's own. */
export type Frame = (props: { crumbs: Crumb[]; children: ReactNode }) => ReactNode

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString() : 'never')
const roleList = (text: string) => text.split(',').map((r) => r.trim()).filter((r) => r.length > 0)
const where = (scope: string, target: string, project: string | null) => (scope === 'app' ? `app ${target}` : scope === 'project' ? `${target} / ${project}` : target)

/** A place typed as `server`, `server/project` or `app:<id>`. */
function placeOf(text: string): Place {
  const t = text.trim()
  if (t.startsWith('app:')) return { app: t.slice(4) }
  const [server, project] = t.split('/', 2)
  return project ? { server, project } : { server }
}

/**
 * The identity home's administration (plan 13a U21), on whatever hosts the home (a Gateway's identity module, or `dbdatakit identity run`), through the console's relay: people, groups, role assignments, external group mappings,
 * app clients, service accounts and tokens. Every call needs the admin role on that server; the server refuses anyone else.
 */
export function IdentityAdminPage({ Frame }: { Frame: Frame }) {
  const isAdmin = useIsAdmin()
  const client = useQueryClient()
  const key = (k: string) => ['identity-home', k]
  const people = useQuery({ queryKey: key('people'), queryFn: identityApi.people.list, enabled: isAdmin })
  const groups = useQuery({ queryKey: key('groups'), queryFn: identityApi.groups.list, enabled: isAdmin })
  const assignments = useQuery({ queryKey: key('assignments'), queryFn: identityApi.assignments.list, enabled: isAdmin })
  const mappings = useQuery({ queryKey: key('mappings'), queryFn: identityApi.mappings.list, enabled: isAdmin })
  const clients = useQuery({ queryKey: key('clients'), queryFn: identityApi.clients.list, enabled: isAdmin })
  const accounts = useQuery({ queryKey: key('service-accounts'), queryFn: identityApi.serviceAccounts.list, enabled: isAdmin })
  const tokens = useQuery({ queryKey: key('tokens'), queryFn: identityApi.tokens.list, enabled: isAdmin })
  const refresh = () => client.invalidateQueries({ queryKey: ['identity-home'] })
  const change = useMutation({ mutationFn: (action: () => Promise<unknown>) => action(), onSuccess: refresh })
  const run = (action: () => Promise<unknown>) => change.mutate(action)

  const [invite, setInvite] = useState({ email: '', role: 'viewer' as 'admin' | 'viewer', link: '' })
  const [assignment, setAssignment] = useState({ who: '', place: '', roles: '' })
  const [mapping, setMapping] = useState({ source: 'entra', group: '', place: '', roles: '' })
  const [groupName, setGroupName] = useState('')
  const [account, setAccount] = useState({ name: '', description: '' })

  const crumbs: Crumb[] = [{ label: 'Identity home', heading: true }]
  if (!isAdmin) return <Frame crumbs={crumbs}><div className="pane"><div className="empty">This screen is for administrators.</div></div></Frame>

  const nameOf = (id: string) => people.data?.find((p) => p.id === id)?.name ?? accounts.data?.find((a) => a.id === id)?.name ?? id
  const error = people.error ?? groups.error ?? assignments.error ?? mappings.error ?? clients.error ?? accounts.error ?? tokens.error ?? change.error
  const row = (columns: string) => ({ className: 'grid-row', style: { gridTemplateColumns: columns, gap: 14 } })

  return (
    <Frame crumbs={crumbs}>
      <div className="pane" data-testid="identity-admin">
        <div className="page-head"><h1 className="page-title">Identity home</h1><span className="page-note">Who people are and what they may do, for every console and app of the install.</span></div>
        <ErrorBanner error={error} />

        <h2 className="card-title">People</h2>
        <div className="card flush" data-testid="identity-people">
          {(people.data ?? []).length === 0 && <div className="empty">Nobody yet.</div>}
          {(people.data ?? []).map((p) => (
            <div key={p.id} {...row('2fr 2fr 2fr 1fr')} data-testid={`identity-person-${p.id}`}>
              <span>{p.name}</span><span className="mono">{p.email ?? 'no address'}</span><span>{p.ways_in.join(', ') || 'no way in yet'}</span>
              {p.enabled
                ? <button type="button" className="btn" onClick={() => run(() => identityApi.people.disable(p.id))} data-testid="identity-person-disable">Disable</button>
                : <button type="button" className="btn" onClick={() => run(() => identityApi.people.enable(p.id))} data-testid="identity-person-enable">Enable</button>}
            </div>
          ))}
          <form {...row('3fr 1fr 1fr')} onSubmit={(e) => { e.preventDefault(); change.mutate(async () => { const made = await identityApi.people.invite(invite.email, invite.role); setInvite({ ...invite, email: '', link: made.url }) }) }}>
            <input className="input" placeholder="address to invite" value={invite.email} onChange={(e) => setInvite({ ...invite, email: e.target.value })} data-testid="identity-invite-email" />
            <select className="input" value={invite.role} onChange={(e) => setInvite({ ...invite, role: e.target.value as 'admin' | 'viewer' })}><option value="viewer">viewer</option><option value="admin">admin</option></select>
            <button type="submit" className="btn" data-testid="identity-invite-make">Invite</button>
          </form>
          {invite.link && <div className="empty mono" data-testid="identity-invite-link">{invite.link}</div>}
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>Role assignments</h2>
        <div className="card flush" data-testid="identity-assignments">
          {(assignments.data ?? []).length === 0 && <div className="empty">No role is assigned.</div>}
          {(assignments.data ?? []).map((a) => (
            <div key={a.id} {...row('2fr 2fr 2fr 1fr')} data-testid={`identity-assignment-${a.subject_name}`}>
              <span>{a.subject_kind === 'group' ? `group ${a.subject_name}` : a.subject_name}</span><span className="mono">{where(a.scope, a.target, a.project)}</span><span>{a.roles.join(', ')}</span>
              <button type="button" className="btn" onClick={() => run(() => identityApi.assignments.remove(a.id))} data-testid="identity-assignment-remove">Remove</button>
            </div>
          ))}
          <form {...row('2fr 2fr 2fr 1fr')} onSubmit={(e) => {
            e.preventDefault()
            const group = groups.data?.find((g) => g.name === assignment.who)
            const who = group ? { group: group.id } : { user: people.data?.find((p) => p.email === assignment.who || p.name === assignment.who)?.id ?? accounts.data?.find((a) => a.name === assignment.who)?.id ?? assignment.who }
            run(() => identityApi.assignments.add(who, placeOf(assignment.place), roleList(assignment.roles)).then(() => setAssignment({ who: '', place: '', roles: '' })))
          }}>
            <input className="input" placeholder="person, group or service account" value={assignment.who} onChange={(e) => setAssignment({ ...assignment, who: e.target.value })} data-testid="identity-assignment-who" />
            <input className="input" placeholder="server, server/project or app:<id>" value={assignment.place} onChange={(e) => setAssignment({ ...assignment, place: e.target.value })} data-testid="identity-assignment-place" />
            <input className="input" placeholder="roles, comma-separated" value={assignment.roles} onChange={(e) => setAssignment({ ...assignment, roles: e.target.value })} data-testid="identity-assignment-roles" />
            <button type="submit" className="btn" data-testid="identity-assignment-add">Assign</button>
          </form>
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>External group mappings</h2>
        <div className="card flush" data-testid="identity-mappings">
          {(mappings.data ?? []).length === 0 && <div className="empty">No Entra or Windows group gives a role.</div>}
          {(mappings.data ?? []).map((m) => (
            <div key={m.id} {...row('1fr 3fr 2fr 2fr 1fr')} data-testid={`identity-mapping-${m.group}`}>
              <span>{m.source}</span><span className="mono">{m.group}</span><span className="mono">{where(m.scope, m.target, m.project)}</span><span>{m.roles.join(', ')}</span>
              <button type="button" className="btn" onClick={() => run(() => identityApi.mappings.remove(m.id))} data-testid="identity-mapping-remove">Remove</button>
            </div>
          ))}
          <form {...row('1fr 3fr 2fr 2fr 1fr')} onSubmit={(e) => { e.preventDefault(); run(() => identityApi.mappings.add(mapping.source, mapping.group, placeOf(mapping.place), roleList(mapping.roles)).then(() => setMapping({ ...mapping, group: '', place: '', roles: '' }))) }}>
            <select className="input" value={mapping.source} onChange={(e) => setMapping({ ...mapping, source: e.target.value })}><option value="entra">entra</option><option value="windows">windows</option></select>
            <input className="input" placeholder="group id or DOMAIN\group" value={mapping.group} onChange={(e) => setMapping({ ...mapping, group: e.target.value })} data-testid="identity-mapping-group" />
            <input className="input" placeholder="server, server/project or app:<id>" value={mapping.place} onChange={(e) => setMapping({ ...mapping, place: e.target.value })} data-testid="identity-mapping-place" />
            <input className="input" placeholder="roles" value={mapping.roles} onChange={(e) => setMapping({ ...mapping, roles: e.target.value })} data-testid="identity-mapping-roles" />
            <button type="submit" className="btn" data-testid="identity-mapping-add">Map</button>
          </form>
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>Groups</h2>
        <div className="card flush" data-testid="identity-groups">
          {(groups.data ?? []).length === 0 && <div className="empty">No group yet.</div>}
          {(groups.data ?? []).map((g) => (
            <div key={g.id} {...row('2fr 4fr 1fr')} data-testid={`identity-group-${g.name}`}>
              <span>{g.name}</span><span>{g.members.map(nameOf).join(', ') || 'no member'}</span>
              <button type="button" className="btn" onClick={() => run(() => identityApi.groups.remove(g.id))} data-testid="identity-group-remove">Remove</button>
            </div>
          ))}
          <form {...row('6fr 1fr')} onSubmit={(e) => { e.preventDefault(); run(() => identityApi.groups.create(groupName).then(() => setGroupName(''))) }}>
            <input className="input" placeholder="new group's name" value={groupName} onChange={(e) => setGroupName(e.target.value)} data-testid="identity-group-name" />
            <button type="submit" className="btn" data-testid="identity-group-add">Make</button>
          </form>
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>Consoles and apps</h2>
        <div className="card flush" data-testid="identity-clients">
          {(clients.data ?? []).length === 0 && <div className="empty">No console or app signs in through the home yet.</div>}
          {(clients.data ?? []).map((c) => (
            <div key={c.id} {...row('2fr 4fr 1fr')} data-testid={`identity-client-${c.name}`}>
              <span>{c.name}</span><span className="mono">{c.redirect_uris.join(', ')}</span>
              <button type="button" className="btn" onClick={() => run(() => identityApi.clients.remove(c.id))} data-testid="identity-client-remove">Remove</button>
            </div>
          ))}
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>Service accounts</h2>
        <div className="card flush" data-testid="identity-service-accounts">
          {(accounts.data ?? []).length === 0 && <div className="empty">No service account.</div>}
          {(accounts.data ?? []).map((a) => (
            <div key={a.id} {...row('2fr 3fr 1fr 1fr')} data-testid={`identity-service-account-${a.name}`}>
              <span className="mono">{a.name}</span><span>{a.description}</span><span>{a.active_tokens} token(s)</span>
              <button type="button" className="btn" onClick={() => run(() => identityApi.serviceAccounts.remove(a.name))} data-testid="identity-service-account-remove">Remove</button>
            </div>
          ))}
          <form {...row('2fr 3fr 1fr')} onSubmit={(e) => { e.preventDefault(); run(() => identityApi.serviceAccounts.create(account.name, account.description).then(() => setAccount({ name: '', description: '' }))) }}>
            <input className="input" placeholder="name" value={account.name} onChange={(e) => setAccount({ ...account, name: e.target.value })} data-testid="identity-service-account-name" />
            <input className="input" placeholder="what it is for" value={account.description} onChange={(e) => setAccount({ ...account, description: e.target.value })} />
            <button type="submit" className="btn" data-testid="identity-service-account-add">Make</button>
          </form>
        </div>

        <h2 className="card-title" style={{ marginTop: 24 }}>Tokens</h2>
        <div className="card flush" data-testid="identity-tokens">
          {(tokens.data ?? []).length === 0 && <div className="empty">No token.</div>}
          {(tokens.data ?? []).map((t) => (
            <div key={t.id} {...row('2fr 2fr 1fr 1fr 1fr')} data-testid={`identity-token-${t.id}`}>
              <span>{t.user_name ?? t.user}</span><span>{t.name}</span><span>{t.revoked ? 'revoked' : t.active ? `until ${when(t.expires)}` : 'expired'}</span><span>used {when(t.last_used)}</span>
              {t.active ? <button type="button" className="btn" onClick={() => run(() => identityApi.tokens.revoke(t.id))} data-testid="identity-token-revoke">Revoke</button> : <span />}
            </div>
          ))}
        </div>
      </div>
    </Frame>
  )
}
