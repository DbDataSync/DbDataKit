import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner } from './ErrorBanner'

/** One publisher key a group trusts for a kind (plan 30 PB03): its id, label, who added it and when, and whether it was revoked. */
export interface TrustKey {
  key_id: string
  label: string | null
  added_by: string
  added_at: string
  revoked: boolean
  revoked_by: string | null
}

/** `GET /api/v1/trust/{kind}`: the keys, whether signed versions of the kind are required, and whether the group is marked `test`. */
export interface TrustState {
  kind: string
  required: boolean
  test_group: boolean
  keys: TrustKey[]
}

/**
 * The calls of a server's trust store, for one kind. A server that keeps its own on/off switch for signatures (the runner's `require_signatures`) leaves `setRequired` and `setGroupMode` out and the card shows the keys only.
 * Every write is an administrator's.
 */
export interface TrustApi {
  get: (kind: string) => Promise<TrustState>
  addKey: (kind: string, publicKey: string, label: string) => Promise<unknown>
  revoke: (kind: string, keyId: string) => Promise<unknown>
  setRequired?: (kind: string, required: boolean) => Promise<unknown>
  setGroupMode?: (mode: 'test' | 'production') => Promise<unknown>
}

/** An Ed25519 public key as the add call reads it: 32 bytes written in base64url (43 characters; trailing `=` is left off). */
export function validPublicKey(text: string): boolean {
  return /^[A-Za-z0-9_-]{43}$/.test(text.trim().replace(/=+$/, ''))
}

/** What the signature policy of a kind means, in words. */
export function policyText(kind: string, required: boolean, testGroup: boolean): string {
  if (required) return `Every version of ${kind} must carry a signature from a key listed here; an unsigned version, or one signed with an unknown or revoked key, is refused and the version that serves stays.`
  return testGroup
    ? `This group is marked a test group, so unsigned versions of ${kind} are accepted and recorded as unsigned. A signature that is there is still checked.`
    : `Unsigned versions of ${kind} are accepted and recorded as unsigned (an administrator turned the requirement off). A signature that is there is still checked.`
}

export interface TrustStoreViewProps {
  kind: string
  /** Null while it is not known. */
  state: TrustState | null
  loading: boolean
  error: unknown
  canEdit: boolean
  /** Whether the server has a signature requirement of its own per kind to switch (the shared API's); false leaves the switch out. */
  policyEditable: boolean
  publicKey: string
  label: string
  /** The key a person is about to revoke, for the confirmation. */
  revoking: string | null
  busy: boolean
  onPublicKey: (value: string) => void
  onLabel: (value: string) => void
  onAdd: () => void
  onAskRevoke: (keyId: string | null) => void
  onRevoke: (keyId: string) => void
  onToggleRequired: () => void
}

/** The trust store of one kind without its calls: the policy, the keys with add and revoke. Hook-free so a test can draw it and press its buttons. */
export function TrustStoreView(p: TrustStoreViewProps) {
  const id = `trust-${p.kind}`
  const trimmed = p.publicKey.trim()
  const keyOk = validPublicKey(trimmed)
  return (
    <div className="card" data-testid="trust-store" data-kind={p.kind}>
      <div className="card-body">
        <div className="card-title">Publisher keys for {p.kind}</div>
        <ErrorBanner error={p.error} />
        {p.loading && <div className="empty" data-testid={`${id}-loading`}>Loading...</div>}
        {p.state && (
          <>
            <div data-testid={`${id}-required`} data-value={p.state.required}>Signatures: <strong>{p.state.required ? 'required' : 'optional'}</strong></div>
            <div className="hint" data-testid={`${id}-policy`}>{policyText(p.kind, p.state.required, p.state.test_group)}</div>
            {p.policyEditable && (
              <div className="row" style={{ gap: 12, margin: '8px 0' }}>
                <button type="button" className="btn btn-sm" disabled={!p.canEdit || p.busy} onClick={p.onToggleRequired} data-testid={`${id}-toggle`}>{p.state.required ? 'Make signatures optional' : 'Require signatures'}</button>
              </div>
            )}
            {p.state.keys.length === 0 && <div className="empty" data-testid={`${id}-none`}>No key is trusted for {p.kind} yet{p.state.required ? ', so every publish of it is refused until one is added' : ''}.</div>}
            {p.state.keys.length > 0 && (
              <table className="table" data-testid={`${id}-keys`}>
                <thead><tr><th>Key</th><th>Label</th><th>Added</th><th /></tr></thead>
                <tbody>
                  {p.state.keys.map((k) => (
                    <tr key={k.key_id} data-testid={`${id}-key`} data-key-id={k.key_id} data-revoked={k.revoked}>
                      <td><code>{k.key_id}</code></td>
                      <td>{k.label ?? ''}</td>
                      <td>{k.revoked ? `revoked by ${k.revoked_by ?? 'an administrator'}` : `by ${k.added_by}, ${k.added_at.slice(0, 10)}`}</td>
                      <td>
                        {!k.revoked && p.canEdit && p.revoking !== k.key_id && <button type="button" className="btn btn-sm btn-danger" onClick={() => p.onAskRevoke(k.key_id)} data-testid={`${id}-revoke`}>Revoke</button>}
                        {p.revoking === k.key_id && (
                          <span className="row" style={{ gap: 8 }} data-testid={`${id}-revoke-ask`}>
                            <span className="hint">New versions signed with {k.key_id} are refused; versions it signed stay.</span>
                            <button type="button" className="btn btn-sm" onClick={() => p.onAskRevoke(null)} data-testid={`${id}-revoke-cancel`}>Keep it</button>
                            <button type="button" className="btn btn-sm btn-danger" disabled={p.busy} onClick={() => p.onRevoke(k.key_id)} data-testid={`${id}-revoke-go`}>Revoke it</button>
                          </span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {p.canEdit ? (
              <div style={{ marginTop: 8 }}>
                <div className="hint">Rotating a key is adding the new one and revoking the old. Paste the public key of the publisher (32 bytes, base64url).</div>
                <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
                  <input className="input" placeholder="Public key" value={p.publicKey} onChange={(e) => p.onPublicKey(e.target.value)} data-testid={`${id}-public-key`} aria-label={`Public key to trust for ${p.kind}`} />
                  <input className="input" placeholder="Label" value={p.label} onChange={(e) => p.onLabel(e.target.value)} data-testid={`${id}-label`} aria-label="Label" />
                  <button type="button" className="btn btn-primary btn-sm" disabled={!keyOk || p.busy} onClick={p.onAdd} data-testid={`${id}-add`}>Trust this key</button>
                </div>
                {trimmed.length > 0 && !keyOk && <div className="hint" data-testid={`${id}-key-problem`}>That is not an Ed25519 public key: write the 32 bytes in base64url (43 characters).</div>}
              </div>
            ) : (
              <div className="hint" data-testid={`${id}-why`}>Changing the keys needs the admin role on this server.</div>
            )}
          </>
        )}
      </div>
    </div>
  )
}

/** The trust store of one kind (plan 30 PB03): who may publish it signed, and whether a signature is required. An administrator adds and revokes; everyone else reads. A change is audited by the server. */
export function TrustStore({ kind, api, canEdit }: { kind: string; api: TrustApi; canEdit: boolean }) {
  const client = useQueryClient()
  const key = ['trust', kind]
  const state = useQuery({ queryKey: key, queryFn: () => api.get(kind), staleTime: 0 })
  const [publicKey, setPublicKey] = useState('')
  const [label, setLabel] = useState('')
  const [revoking, setRevoking] = useState<string | null>(null)
  const refresh = () => client.invalidateQueries({ queryKey: key })
  const add = useMutation({ mutationFn: () => api.addKey(kind, publicKey.trim().replace(/=+$/, ''), label.trim()), onSuccess: () => { setPublicKey(''); setLabel(''); void refresh() } })
  const revoke = useMutation({ mutationFn: (keyId: string) => api.revoke(kind, keyId), onSuccess: () => { setRevoking(null); void refresh() } })
  const toggle = useMutation({ mutationFn: () => api.setRequired!(kind, !state.data!.required), onSuccess: () => void refresh() })
  return (
    <TrustStoreView
      kind={kind}
      state={state.data ?? null}
      loading={state.isLoading}
      error={state.error ?? add.error ?? revoke.error ?? toggle.error}
      canEdit={canEdit}
      policyEditable={api.setRequired !== undefined}
      publicKey={publicKey}
      label={label}
      revoking={revoking}
      busy={add.isPending || revoke.isPending || toggle.isPending}
      onPublicKey={setPublicKey}
      onLabel={setLabel}
      onAdd={() => add.mutate()}
      onAskRevoke={setRevoking}
      onRevoke={(keyId) => revoke.mutate(keyId)}
      onToggleRequired={() => toggle.mutate()}
    />
  )
}
