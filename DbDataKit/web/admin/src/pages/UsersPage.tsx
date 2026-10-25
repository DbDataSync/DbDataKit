import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, shellApi, useIsAdmin } from '@dbdatakit/shell'
import type { CreatedInvite, UserSummary } from '@dbdatakit/shell'
import { useAdminHost } from '../host'

const COLUMNS = '1.6fr 1.6fr 0.9fr 0.8fr 2fr'
const usersKey = ['users'] as const

/**
 * Who can sign in, what they may do, and how they get in (plan 13, U07). Administrators only: the API enforces it, and this screen says so rather than showing a viewer a list that would 403.
 * An invitation is made here too: its link is shown once, because the code is hashed the moment it is made.
 */
export function UsersPage() {
  const { Frame } = useAdminHost()
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const users = useQuery({ queryKey: usersKey, queryFn: shellApi.users.list, enabled: isAdmin })
  const update = useMutation({
    mutationFn: ({ id, changes }: { id: string; changes: { role?: string; enabled?: boolean } }) => shellApi.users.update(id, changes),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: usersKey }),
  })
  const removeCredential = useMutation({
    mutationFn: ({ id, credentialId }: { id: string; credentialId: string }) => shellApi.users.removeCredential(id, credentialId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: usersKey }),
  })
  const [inviteRole, setInviteRole] = useState('Viewer')
  const [invite, setInvite] = useState<CreatedInvite | null>(null)
  const makeInvite = useMutation({
    mutationFn: (forUserId?: string) => shellApi.invites.create(inviteRole, forUserId),
    onSuccess: (created) => setInvite(created),
  })

  if (!isAdmin) {
    return (
      <Frame crumbs={[{ label: 'Users', heading: true }]}>
        <div className="pane"><div className="empty">This screen is for administrators.</div></div>
      </Frame>
    )
  }

  return (
    <Frame crumbs={[{ label: 'Users', heading: true }]}>
      <div className="pane" data-testid="users-page">
        <div className="page-head">
          <h1 className="page-title">Users</h1>
          <span className="page-note">Everyone who can sign in here. Each person has a passkey; an invitation lets someone join, or lets someone who lost their passkey back in.</span>
        </div>

        <ErrorBanner error={users.error ?? update.error ?? removeCredential.error ?? makeInvite.error} />

        <div className="card flush" data-testid="users-table">
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Name</span><span>Email</span><span>Role</span><span>Enabled</span><span>Ways to sign in</span>
          </div>
          {users.isLoading && <div className="empty">Loading…</div>}
          {(users.data ?? []).map((user) => (
            <UserRow
              key={user.id}
              user={user}
              busy={update.isPending || removeCredential.isPending}
              onRole={(role) => update.mutate({ id: user.id, changes: { role } })}
              onEnabled={(enabled) => update.mutate({ id: user.id, changes: { enabled } })}
              onRemove={(credentialId) => removeCredential.mutate({ id: user.id, credentialId })}
              onInvite={() => makeInvite.mutate(user.id)}
            />
          ))}
        </div>

        <div className="card" style={{ marginTop: 20 }}>
          <div className="card-head"><span className="card-title">Invite someone</span></div>
          <div className="card-body">
            <div className="row" style={{ gap: 10 }}>
              <select className="input" value={inviteRole} onChange={(e) => setInviteRole(e.target.value)} data-testid="invite-role-select">
                <option value="Viewer">Viewer</option>
                <option value="Admin">Administrator</option>
              </select>
              <button type="button" className="btn btn-primary" disabled={makeInvite.isPending} onClick={() => makeInvite.mutate(undefined)} data-testid="invite-create-button">
                Make an invitation link
              </button>
            </div>
            {invite && (
              <div style={{ marginTop: 12 }} data-testid="invite-created">
                <span className="hint">Send this link to the person. It works once, until {new Date(invite.expiresAtUtc).toLocaleString()}, and cannot be shown again.</span>
                <input className="input mono" readOnly value={invite.url} onFocus={(e) => e.currentTarget.select()} data-testid="invite-link" />
              </div>
            )}
          </div>
        </div>
      </div>
    </Frame>
  )
}

function UserRow({ user, busy, onRole, onEnabled, onRemove, onInvite }: {
  user: UserSummary
  busy: boolean
  onRole: (role: string) => void
  onEnabled: (enabled: boolean) => void
  onRemove: (credentialId: string) => void
  onInvite: () => void
}) {
  return (
    <div className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 14 }} data-testid={`user-row-${user.displayName}`}>
      <span>{user.displayName}</span>
      <span className="mono">{user.email ?? '—'}</span>
      <select className="input" value={user.role} disabled={busy} onChange={(e) => onRole(e.target.value)} data-testid="user-role-select">
        <option value="Viewer">Viewer</option>
        <option value="Admin">Administrator</option>
      </select>
      <input type="checkbox" checked={user.enabled} disabled={busy} onChange={(e) => onEnabled(e.target.checked)} data-testid="user-enabled-checkbox" />
      <span className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
        {user.credentials.map((credential) => (
          <span key={credential.id} className="badge" data-testid="user-credential">
            {credential.label ?? credential.method}
            <button type="button" className="btn btn-chrome" disabled={busy || user.credentials.length <= 1} title="Remove this way to sign in" onClick={() => onRemove(credential.id)}>×</button>
          </span>
        ))}
        <button type="button" className="btn" disabled={busy} onClick={onInvite} data-testid="user-invite-button">New invitation</button>
      </span>
    </div>
  )
}
