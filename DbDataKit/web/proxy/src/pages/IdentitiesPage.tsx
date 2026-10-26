import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { proxyApi } from '../api'
import { ProxyShell } from '../ProxyShell'
import type { JwtMapping, UserRow } from '../types'

/** Which values of a token's claim may use which data sources. Saved as a whole list; the rest of the JWT settings (key, authority, audience) stay in proxy/auth.yaml. */
function JwtMappings({ initial, version, onSaved }: { initial: JwtMapping[]; version: string | null; onSaved: () => void }) {
  const [rows, setRows] = useState<JwtMapping[]>(initial)
  const save = useMutation({ mutationFn: () => proxyApi.identities.setJwtMappings(rows, version), onSuccess: onSaved })
  const change = (i: number, patch: Partial<JwtMapping>) => setRows(rows.map((r, j) => (j === i ? { ...r, ...patch } : r)))
  return (
    <div data-testid="jwt-mappings">
      <ErrorBanner error={save.error} />
      {rows.map((r, i) => (
        <div key={i} className="row" style={{ gap: 8, marginBottom: 8, flexWrap: 'wrap' }} data-testid={`jwt-mapping-${i}`}>
          <input className="input" placeholder="claim" value={r.claim} onChange={(e) => change(i, { claim: e.target.value })} data-testid="jwt-claim-input" />
          <input className="input" placeholder="value" value={r.value} onChange={(e) => change(i, { value: e.target.value })} data-testid="jwt-value-input" />
          <input className="input" placeholder="data sources (* for all)" value={r.data_sources.join(', ')} onChange={(e) => change(i, { data_sources: e.target.value.split(',').map((s) => s.trim()).filter(Boolean) })} data-testid="jwt-sources-input" />
          <input className="input" placeholder="default data source" value={r.default_data_source ?? ''} onChange={(e) => change(i, { default_data_source: e.target.value || null })} />
          <button type="button" className="btn" onClick={() => setRows(rows.filter((_, j) => j !== i))} data-testid="jwt-mapping-remove">Remove</button>
        </div>
      ))}
      <div className="row" style={{ gap: 8 }}>
        <button type="button" className="btn" onClick={() => setRows([...rows, { claim: '', value: '', data_sources: [], default_data_source: null }])} data-testid="jwt-mapping-add">Add a mapping</button>
        <button type="button" className="btn btn-primary" disabled={save.isPending} onClick={() => save.mutate()} data-testid="jwt-mappings-save">Save mappings</button>
      </div>
    </div>
  )
}

/** Who may use the proxy: the Flight users and Windows accounts (a password is typed here, hashed on the server and never shown again), pass-through, and the JWT settings as they are in effect. */
export function IdentitiesPage() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const identities = useQuery({ queryKey: ['proxy', 'identities'], queryFn: proxyApi.identities.get, enabled: isAdmin })
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['proxy'] })
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [windows, setWindows] = useState(false)
  const [sources, setSources] = useState('')
  const save = useMutation({
    mutationFn: () => {
      const existing = [...(identities.data?.users ?? []), ...(identities.data?.windows_accounts ?? [])].find((u) => u.name.toLowerCase() === name.toLowerCase())
      return proxyApi.identities.writeUser(name, {
        auth_type: windows ? 'windows' : 'password', password: windows || password === '' ? undefined : password,
        data_sources: sources.split(',').map((s) => s.trim()).filter(Boolean), expected_version: existing?.version ?? null,
      })
    },
    onSuccess: async () => { setName(''); setPassword(''); setSources(''); await refresh() },
  })
  const remove = useMutation({ mutationFn: (u: UserRow) => proxyApi.identities.removeUser(u.name, u.version), onSuccess: refresh })
  const passThrough = useMutation({ mutationFn: (allow: boolean) => proxyApi.identities.setPassThrough(allow, identities.data?.auth_version ?? null), onSuccess: refresh })

  if (!isAdmin) return <ProxyShell crumbs={[{ label: 'Users and sign-in', heading: true }]}><div className="pane"><div className="empty">This screen is for administrators.</div></div></ProxyShell>
  const data = identities.data
  const rows = [...(data?.users ?? []), ...(data?.windows_accounts ?? [])]
  return (
    <ProxyShell crumbs={[{ label: 'Users and sign-in', heading: true }]}>
      <div className="pane" data-testid="proxy-identities">
        <div className="page-head">
          <h1 className="page-title">Users and sign-in</h1>
          <span className="page-note">Who may query, and how they prove who they are. A password is hashed here and never shown again.</span>
        </div>
        <ErrorBanner error={identities.error ?? save.error ?? remove.error ?? passThrough.error} />
        <div className="card flush" data-testid="users-table">
          <div className="grid-head" style={{ gridTemplateColumns: '1.6fr 1fr 1fr 2fr 1fr', gap: 14 }}><span>Name</span><span>Sign-in</span><span>Password</span><span>Data sources</span><span></span></div>
          {rows.length === 0 && !identities.isLoading && <div className="empty">No user yet.</div>}
          {rows.map((u) => (
            <div key={u.name} className="grid-row" style={{ gridTemplateColumns: '1.6fr 1fr 1fr 2fr 1fr', gap: 14 }} data-testid={`user-${u.name}`}>
              <span className="mono">{u.name}</span><span>{u.auth_type === 'windows' ? 'Windows' : 'Password'}</span><span data-testid="user-has-password">{u.has_password ? 'set' : '—'}</span>
              <span className="mono">{u.data_sources.join(', ') || '—'}</span>
              <button type="button" className="btn" disabled={remove.isPending} onClick={() => remove.mutate(u)} data-testid="user-remove">Remove</button>
            </div>
          ))}
        </div>

        <div className="card" style={{ marginTop: 20 }}>
          <div className="card-head"><span className="card-title">Add or change a user</span></div>
          <div className="card-body">
            <div className="row" style={{ gap: 10, flexWrap: 'wrap' }}>
              <input className="input" placeholder="name (DOMAIN\account for Windows)" value={name} onChange={(e) => setName(e.target.value)} data-testid="user-name-input" />
              <label className="row" style={{ gap: 6 }}><input type="checkbox" checked={windows} onChange={(e) => setWindows(e.target.checked)} data-testid="user-windows-checkbox" /> Windows account</label>
              {!windows && <input className="input" type="password" autoComplete="new-password" placeholder="password (empty keeps the current)" value={password} onChange={(e) => setPassword(e.target.value)} data-testid="user-password-input" />}
              <input className="input" placeholder="data sources, comma separated (* for all)" value={sources} onChange={(e) => setSources(e.target.value)} data-testid="user-sources-input" />
              <button type="button" className="btn btn-primary" disabled={save.isPending || name.trim() === ''} onClick={() => save.mutate()} data-testid="user-save">Save</button>
            </div>
          </div>
        </div>

        {data && (
          <div className="card" style={{ marginTop: 20 }} data-testid="signin-settings">
            <div className="card-head"><span className="card-title">Other ways in</span></div>
            <div className="card-body">
              <label className="row" style={{ gap: 8 }}>
                <input type="checkbox" checked={data.pass_through} disabled={passThrough.isPending} onChange={(e) => passThrough.mutate(e.target.checked)} data-testid="pass-through-checkbox" />
                Let a caller's own database login through (pass-through data sources check it by connecting as that user)
              </label>
              <p style={{ marginTop: 12 }} data-testid="jwt-summary">
                {data.jwt.enabled
                  ? `JWT bearer tokens are accepted${data.jwt.authority ? ` from ${data.jwt.authority}` : ' with a shared key'}${data.jwt.has_signing_key ? ' (a signing key is set)' : ''}, audience ${data.jwt.audience ?? '—'}. The key, authority and audience are in proxy/auth.yaml.`
                  : 'JWT bearer tokens are not accepted. Turn them on in proxy/auth.yaml; the mappings below apply once they are.'}
              </p>
              <JwtMappings key={JSON.stringify(data.jwt.mappings)} initial={data.jwt.mappings} version={data.auth_version} onSaved={refresh} />
            </div>
          </div>
        )}
      </div>
    </ProxyShell>
  )
}
