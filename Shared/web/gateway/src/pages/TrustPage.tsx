import type { ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, TrustStore, useIsAdmin } from '@dbdatakit/shell'
import { gatewayApi } from '../api'
import { GatewayShell } from '../GatewayShell'
import { TRUST_KINDS, groupModeText } from '../trust'

export interface TrustViewProps {
  /** Whether the group is marked `test` (null while not known). */
  testGroup: boolean | null
  canEdit: boolean
  busy: boolean
  error: unknown
  onMode: (mode: 'test' | 'production') => void
  /** The card of one kind (the real one draws a `TrustStore`, a test draws a marker). */
  card: (kind: string) => ReactNode
}

/** The page without its calls: the group's mode and one card of publisher keys for every kind a publisher can send here. Hook-free so a test can draw it and press its button. */
export function TrustView(p: TrustViewProps) {
  return (
    <div className="pane" data-testid="gateway-trust">
      <div className="page-head"><h1 className="page-title">Publishers</h1><span className="page-note">Who may publish what this gateway serves: the keys of the publishers that sign, and whether a signature is required.</span></div>
      <ErrorBanner error={p.error} />
      {p.testGroup !== null && (
        <div className="card" data-testid="trust-group"><div className="card-body">
          <div className="card-title">This group is a {p.testGroup ? 'test' : 'production'} group</div>
          <div className="hint" data-testid="trust-group-text">{groupModeText(p.testGroup)}</div>
          {p.canEdit && <button type="button" className="btn btn-sm" disabled={p.busy} onClick={() => p.onMode(p.testGroup ? 'production' : 'test')} data-testid="trust-group-toggle">{p.testGroup ? 'Mark it production' : 'Mark it a test group'}</button>}
        </div></div>
      )}
      {TRUST_KINDS.map((kind) => <div key={kind} style={{ marginTop: 12 }}>{p.card(kind)}</div>)}
    </div>
  )
}

/** The trust store of the serving kinds (plan 30 PB03): add, rotate and revoke the publisher keys of each kind, set whether signatures are required for it, and mark the group a test group. Changes are an administrator's and are audited. */
export function TrustPage() {
  const isAdmin = useIsAdmin()
  const client = useQueryClient()
  const first = useQuery({ queryKey: ['trust', TRUST_KINDS[0]], queryFn: () => gatewayApi.trust.get(TRUST_KINDS[0]), staleTime: 0 })
  const mode = useMutation({ mutationFn: (m: 'test' | 'production') => gatewayApi.trust.setGroupMode(m), onSuccess: () => client.invalidateQueries({ queryKey: ['trust'] }) })
  return (
    <GatewayShell crumbs={[{ label: 'Publishers', heading: true }]}>
      <TrustView
        testGroup={first.data?.test_group ?? null}
        canEdit={isAdmin}
        busy={mode.isPending}
        error={mode.error}
        onMode={(m) => mode.mutate(m)}
        card={(kind) => <TrustStore kind={kind} api={gatewayApi.trust} canEdit={isAdmin} />}
      />
    </GatewayShell>
  )
}
