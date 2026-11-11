import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { PairedProxy, PublishModelAnswer, SemanticModelEntry } from '../types'
import { publishFailure } from './PublishTab'

/** Why the button is off, in words, or null. */
export function publishModelBlock(proxy: string, model: string, busy: boolean): string | null {
  if (busy) return 'Publishing…'
  if (proxy === '') return 'Choose the Gateway to publish to.'
  if (model === '') return 'Choose a semantic model of this project.'
  return null
}

/** The connections a model reads, as the sentence under the chooser says them: they are published with it. */
export const readsOf = (m: SemanticModelEntry | undefined): string =>
  !m ? '' : [...new Set(m.sources.map((s) => s.connection))].sort().join(', ')

export interface ModelPublishViewProps {
  proxies: PairedProxy[] | undefined
  models: SemanticModelEntry[] | undefined
  loading: boolean
  listError: unknown
  proxy: string
  model: string
  busy: boolean
  done: PublishModelAnswer | null
  failure: unknown
  onProxy: (name: string) => void
  onModel: (name: string) => void
  onPublish: () => void
}

/** The screen without its calls: publish a semantic model of the project to a Gateway this server is paired with (plan 33 UE12). */
export function ModelPublishView(p: ModelPublishViewProps) {
  const noProxy = !p.loading && !p.listError && (p.proxies?.length ?? 0) === 0
  const noModel = !p.loading && !p.listError && p.models !== undefined && p.models.length === 0
  const why = publishModelBlock(p.proxy, p.model, p.busy)
  const chosen = p.models?.find((m) => m.name === p.model)
  return (
    <div style={{ marginTop: 28 }} data-testid="build-publish-model">
      <strong>Publish a semantic model</strong>
      <div className="hint" style={{ margin: '4px 0 12px' }} data-testid="build-publish-model-about">
        Sends a semantic model of this project (<span className="mono">semantic/</span>) to a Gateway, with the connections it reads as read-only data sources (only their <strong>read</strong> logins go). The Gateway checks that the model loads and that every table answers; its refusal is shown in its words. Needs the approver role. What the Gateway serves, with its versions, is on the Gateway&apos;s Models screen.
      </div>
      <ErrorBanner error={p.listError} />
      {p.loading && <div className="empty">Loading…</div>}
      {noProxy && <div className="empty" data-testid="build-publish-model-no-gateway">This server is not paired with a Gateway. Link it to one on the console&apos;s Servers page.</div>}
      {noModel && <div className="empty" data-testid="build-publish-model-none">This project has no semantic model (a file under <span className="mono">semantic/</span>).</div>}
      {(p.proxies?.length ?? 0) > 0 && (p.models?.length ?? 0) > 0 && (
        <>
          <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
            <label className="row" style={{ gap: 8 }}>
              <span className="hint">Gateway</span>
              <select className="input" value={p.proxy} onChange={(e) => p.onProxy(e.target.value)} data-testid="build-publish-model-proxy">
                <option value="">Choose…</option>
                {p.proxies!.map((x) => <option key={x.name} value={x.name}>{x.name}</option>)}
              </select>
            </label>
            <label className="row" style={{ gap: 8 }}>
              <span className="hint">Model</span>
              <select className="input mono" value={p.model} onChange={(e) => p.onModel(e.target.value)} data-testid="build-publish-model-name">
                <option value="">Choose…</option>
                {p.models!.map((m) => <option key={m.name} value={m.name}>{m.name}</option>)}
              </select>
            </label>
            <button type="button" className="btn btn-primary" disabled={why !== null} title={why ?? undefined} onClick={p.onPublish} data-testid="build-publish-model-button">{p.busy ? 'Publishing…' : 'Publish the model'}</button>
          </div>
          {chosen && <div className="hint" style={{ marginTop: 6 }} data-testid="build-publish-model-reads">Reads the connection{readsOf(chosen).includes(',') ? 's' : ''} <span className="mono">{readsOf(chosen) || 'none'}</span>{chosen.description ? `. ${chosen.description}` : ''}</div>}
          {why && !p.busy && <div className="hint" style={{ marginTop: 6 }} data-testid="build-publish-model-why">{why}</div>}
        </>
      )}
      {p.failure != null && <div className="banner error" role="alert" style={{ marginTop: 10 }} data-testid="build-publish-model-failed"><span className="mark">!</span><span>{publishFailure(p.failure)}</span></div>}
      {p.done && (
        <div className="card" style={{ marginTop: 10 }} data-testid="build-publish-model-done">
          <div className="card-body">
            <span className="mono">{p.done.model}</span> is {p.done.created ? 'now served' : 'updated'} on <strong>{p.done.proxy}</strong>{p.done.version !== null ? <>, version <span data-testid="build-publish-model-version">{p.done.version}</span></> : null}{p.done.created ? ' (new)' : ''}.
            {p.done.data_sources.length > 0 && <div className="hint">Data sources published with it: <span className="mono">{p.done.data_sources.filter((d) => d).join(', ')}</span>.</div>}
          </div>
        </div>
      )}
    </div>
  )
}

/** Publishing a semantic model of the project to a Gateway, under the connection publish on the Publish tab. */
export function ModelPublish({ project }: { project: string }) {
  const [proxy, setProxy] = useState('')
  const [model, setModel] = useState('')
  const proxies = useQuery({ queryKey: ['build', 'proxies'], queryFn: () => buildApi.proxies() })
  const models = useQuery({ queryKey: ['build', project, 'semantic-models'], queryFn: () => buildApi.semanticModels(project) })
  const publish = useMutation({ mutationFn: () => buildApi.publishModel(project, proxy, model) })
  return (
    <ModelPublishView
      proxies={proxies.data} models={models.data} loading={proxies.isLoading || models.isLoading} listError={proxies.error ?? models.error}
      proxy={proxy} model={model} busy={publish.isPending} done={publish.data ?? null} failure={publish.error}
      onProxy={(v) => { setProxy(v); publish.reset() }} onModel={(v) => { setModel(v); publish.reset() }} onPublish={() => publish.mutate()}
    />
  )
}
