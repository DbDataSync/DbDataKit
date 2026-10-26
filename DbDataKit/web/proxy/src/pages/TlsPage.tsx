import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { proxyApi } from '../api'
import { ProxyShell } from '../ProxyShell'

/** The certificate the proxy presents, and (for a self-signed one) renewing it now. Renewing replaces the certificate every paired console pinned, so it asks first. */
export function TlsPage() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const certificate = useQuery({ queryKey: ['proxy', 'certificate'], queryFn: proxyApi.certificate.get, enabled: isAdmin })
  const [confirming, setConfirming] = useState(false)
  const renew = useMutation({ mutationFn: proxyApi.certificate.renew, onSuccess: async () => { setConfirming(false); await queryClient.invalidateQueries({ queryKey: ['proxy'] }) } })
  if (!isAdmin) return <ProxyShell crumbs={[{ label: 'Certificate', heading: true }]}><div className="pane"><div className="empty">This screen is for administrators.</div></div></ProxyShell>
  const c = certificate.data
  return (
    <ProxyShell crumbs={[{ label: 'Certificate', heading: true }]}>
      <div className="pane" data-testid="proxy-tls">
        <div className="page-head"><h1 className="page-title">Certificate</h1><span className="page-note">What the proxy presents to clients and to the console.</span></div>
        <ErrorBanner error={certificate.error ?? renew.error} />
        {c && !c.present && <div className="empty" data-testid="tls-none">TLS is off, so there is no certificate.</div>}
        {c?.present && (
          <div className="card" data-testid="tls-details">
            <div className="card-body">
              <table className="kv" data-testid="tls-table"><tbody>
                {([
                  ['Source', c.mode], ['Subject', c.subject ?? ''], ['Self-signed', c.self_signed ? 'yes' : 'no'], ['Thumbprint', c.thumbprint ?? ''],
                  ['Valid until', c.not_after ? new Date(c.not_after).toLocaleString() : ''], ['Days left', String(c.days_left ?? '')], ['Renewal due', c.needs_renewal ? 'yes' : 'no'], ['Names', (c.names ?? []).join(', ')],
                ] as const).map(([k, v]) => <tr key={k}><th style={{ textAlign: 'left', paddingRight: 24 }}>{k}</th><td className="mono">{v}</td></tr>)}
              </tbody></table>
            </div>
          </div>
        )}
        {c?.present && c.mode === 'selfsigned' && (
          <div style={{ marginTop: 16 }}>
            {!confirming
              ? <button type="button" className="btn" onClick={() => setConfirming(true)} data-testid="tls-renew">Renew now…</button>
              : (
                <div className="card" data-testid="tls-renew-confirm"><div className="card-body">
                  <p>Renewing replaces the certificate every paired console has pinned. Each of them has to be paired with this proxy again.</p>
                  <div className="row" style={{ gap: 10 }}>
                    <button type="button" className="btn btn-primary" disabled={renew.isPending} onClick={() => renew.mutate()} data-testid="tls-renew-yes">Renew and pair again</button>
                    <button type="button" className="btn" onClick={() => setConfirming(false)}>Not now</button>
                  </div>
                </div></div>
              )}
          </div>
        )}
      </div>
    </ProxyShell>
  )
}
