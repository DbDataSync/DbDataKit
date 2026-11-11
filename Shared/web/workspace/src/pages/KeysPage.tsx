import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ErrorBanner, useIsAdmin } from '@dbdatakit/shell'
import { workspaceApi } from '../api'
import { pairProblem, referenceProblem } from '../model'
import { WorkspaceShell } from '../WorkspaceShell'

const when = (iso: string) => (iso ? new Date(iso).toLocaleString() : '')

/**
 * The publisher keys (an administrator's): for each target a version is published to, the **reference** to the secret that holds its key, and the Gateway it is paired with. The key itself is in the server's secret store and
 * is never shown or typed here; Pair makes one and pairs it with a Gateway using the single-use code that Gateway made (`server pair --max-role publisher`). Every change is in the server's audit.
 */
export function KeysPage() {
  const client = useQueryClient()
  const canWrite = useIsAdmin()
  const keys = useQuery({ queryKey: ['workspace', 'keys'], queryFn: workspaceApi.publisherKeys, enabled: canWrite, retry: false })
  const [name, setName] = useState('')
  const [url, setUrl] = useState('')
  const [code, setCode] = useState('')
  const [refName, setRefName] = useState('')
  const [reference, setReference] = useState('')
  const [asking, setAsking] = useState<string | null>(null)
  const refresh = () => client.invalidateQueries({ queryKey: ['workspace', 'keys'] })
  const pair = useMutation({ mutationFn: () => workspaceApi.pairKey(name.trim(), url.trim(), code.trim()), onSuccess: async () => { setName(''); setUrl(''); setCode(''); await refresh() } })
  const set = useMutation({ mutationFn: () => workspaceApi.setKey(refName.trim(), reference.trim()), onSuccess: async () => { setRefName(''); setReference(''); await refresh() } })
  const remove = useMutation({ mutationFn: (key: string) => workspaceApi.removeKey(key), onSuccess: async () => { setAsking(null); await refresh() } })
  const pairWhy = pairProblem({ name: name.trim(), url })
  const refWhy = referenceProblem(reference)
  const refNameWhy = refName !== '' ? pairProblem({ name: refName.trim(), url: '' }) : null
  return (
    <WorkspaceShell crumbs={[{ label: 'Workspaces', to: '/' }, { label: 'Publisher keys', heading: true }]}>
      <div className="pane" data-testid="wsk-page">
        <div className="page-head"><h1 className="page-title">Publisher keys</h1></div>
        <div className="hint">A key is what a publish is signed with. It is kept in this server's secret store; here you see only the reference to it and the Gateway it is paired with. Every change is audited.</div>
        {!canWrite && <div className="empty" data-testid="wsk-denied">Publisher keys are an administrator's: the references and the pairing of a key are not shown to anyone else.</div>}
        {canWrite && (
          <>
            <ErrorBanner error={keys.error} />
            {keys.data?.length === 0 && <div className="empty" data-testid="wsk-none">There is no publisher key. Pair one with a Gateway below.</div>}
            {(keys.data?.length ?? 0) > 0 && (
              <table className="table" data-testid="wsk-table">
                <thead><tr><th>Target</th><th>Key reference</th><th>Gateway</th><th>Roles there</th><th>Changed</th><th /></tr></thead>
                <tbody>
                  {keys.data!.map((k) => (
                    <tr key={k.name} data-testid="wsk-key" data-key={k.name}>
                      <td className="mono">{k.name}</td>
                      <td className="mono" data-testid="wsk-ref">{k.secret_ref}</td>
                      <td data-testid="wsk-gateway">{k.gateway?.url ?? <span className="hint">not paired</span>}</td>
                      <td>{k.gateway?.roles.join(', ') ?? ''}</td>
                      <td className="hint">{when(k.updated)}</td>
                      <td>
                        {asking !== k.name && <button type="button" className="btn" data-testid={`wsk-remove-${k.name}`} onClick={() => setAsking(k.name)}>Remove</button>}
                        {asking === k.name && (
                          <>
                            <span className="hint">The reference goes; the secret stays in the store.</span>
                            <button type="button" className="btn btn-danger" disabled={remove.isPending} data-testid={`wsk-remove-confirm-${k.name}`} onClick={() => remove.mutate(k.name)}>Remove it</button>
                            <button type="button" className="btn" onClick={() => setAsking(null)}>Keep it</button>
                          </>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <ErrorBanner error={remove.error} />
            <div className="card" style={{ marginTop: 14 }}><div className="card-body">
              <div className="card-title">Pair a key with a Gateway</div>
              <div className="hint">On the Gateway: <span className="mono">dbdatagateway server pair --max-role publisher</span>, then give its management address and the single-use code.</div>
              <form className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 8 }} onSubmit={(e) => { e.preventDefault(); if (!pairWhy && name.trim() && url.trim() && code.trim()) pair.mutate() }}>
                <input className="input mono" aria-label="Target name" placeholder="target" value={name} onChange={(e) => setName(e.target.value)} data-testid="wsk-pair-name" />
                <input className="input mono" style={{ minWidth: 260 }} aria-label="Gateway address" placeholder="https://gateway:8443" value={url} onChange={(e) => setUrl(e.target.value)} data-testid="wsk-pair-url" />
                <input className="input mono" aria-label="Pairing code" placeholder="pairing code" autoComplete="off" value={code} onChange={(e) => setCode(e.target.value)} data-testid="wsk-pair-code" />
                <button type="submit" className="btn btn-primary" disabled={!!pairWhy || !name.trim() || !url.trim() || !code.trim() || pair.isPending} data-testid="wsk-pair">Pair</button>
                {pairWhy && <span className="hint" data-testid="wsk-pair-why">{pairWhy}</span>}
              </form>
              <ErrorBanner error={pair.error} />
              {pair.isSuccess && <div className="hint" data-testid="wsk-paired">Paired {pair.data.name} with {pair.data.gateway?.url}. The key is in the secret store as {pair.data.secret_ref}.</div>}
            </div></div>
            <div className="card" style={{ marginTop: 14 }}><div className="card-body">
              <div className="card-title">Point a target at a key that is already in the secret store</div>
              <form className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginTop: 8 }} onSubmit={(e) => { e.preventDefault(); if (!refWhy && !refNameWhy && refName.trim() && reference.trim()) set.mutate() }}>
                <input className="input mono" aria-label="Target name for the reference" placeholder="target" value={refName} onChange={(e) => setRefName(e.target.value)} data-testid="wsk-ref-name" />
                <input className="input mono" style={{ minWidth: 300 }} aria-label="Reference to the secret" placeholder="secret:dbdatakit:workspace:publisher-key:edge" value={reference} onChange={(e) => setReference(e.target.value)} data-testid="wsk-ref-value" />
                <button type="submit" className="btn" disabled={!!refWhy || !!refNameWhy || !refName.trim() || !reference.trim() || set.isPending} data-testid="wsk-ref-set">Set the reference</button>
                {(refWhy ?? refNameWhy) && <span className="hint" data-testid="wsk-ref-why">{refWhy ?? refNameWhy}</span>}
              </form>
              <ErrorBanner error={set.error} />
            </div></div>
          </>
        )}
      </div>
    </WorkspaceShell>
  )
}
