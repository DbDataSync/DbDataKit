import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ErrorBanner } from '@dbdatakit/shell'
import { EntityEditor, StoreClientProvider, createStoreClient, useSchema, type Fetcher } from '@dbdatakit/store-ui'
import { workspaceApi, type PublishTarget } from '../api'
import { modelsOf } from './StoreDeployPanel'

/** The Gateways a model's rows can be edited on: paired targets that are Gateways and name their instance (the console's relay is by instance). */
export const gatewaysOf = (targets: readonly PublishTarget[]): PublishTarget[] => targets.filter((t) => t.product === 'gateway' && !!t.instance)

/** Where the store controls send their calls for a Gateway: its store paths on the management API, through the console's relay as the signed-in person (plan 33 SC24). */
export const storeBaseOf = (instance: string): string => `/relay/${encodeURIComponent(instance)}/api/v1/store`

/** The relay's own fetch: the person's session cookie, nothing else. */
const relayFetcher: Fetcher = (path, init) => fetch(path, { ...init, credentials: 'same-origin' })

function Entities({ model, canWrite }: { model: string; canWrite: boolean }) {
  const schema = useSchema(model)
  const [chosen, setChosen] = useState('')
  const entities = schema.data?.entities ?? []
  const entity = chosen !== '' && entities.some((e) => e.name === chosen) ? chosen : (entities[0]?.name ?? '')
  if (schema.loading) return <div className="hint">Loading the model from the Gateway...</div>
  if (schema.error) return <ErrorBanner error={schema.error} />
  if (entities.length === 0) return <div className="hint" data-testid="wsp-rows-no-entity">The Gateway serves no entity of {model} that you may read.</div>
  return (
    <>
      <select className="input mono" aria-label="Entity" value={entity} onChange={(e) => setChosen(e.target.value)} data-testid="wsp-rows-entity" style={{ marginBottom: 8 }}>
        {entities.map((e) => <option key={e.name} value={e.name}>{e.title ?? e.name}</option>)}
      </select>
      <EntityEditor key={`${model}/${entity}`} model={model} entity={entity} canWrite={canWrite} />
    </>
  )
}

/**
 * The Rows tab of a Store project's workspace (plan 33 SC24): an entity's rows of a published model, read and written with the shared `store-ui` controls against a paired Gateway's store (the group serves the model's default copy), as the
 * signed-in person through the console's relay. Every rule is the Gateway's: who may read and write, a change request when the model asks for approval (the console's Approvals page decides it), and the audit names the person. The model must have
 * been published to that Gateway (the Builds tab) and its tables deployed (the Deploy tab).
 */
export function StoreRowsPanel({ project, paths, canWrite }: { project: string; paths: readonly string[]; canWrite: boolean }) {
  const models = modelsOf(paths)
  const [chosenModel, setModel] = useState('')
  const model = chosenModel !== '' && models.includes(chosenModel) ? chosenModel : (models[0] ?? '')
  const targets = useQuery({ queryKey: ['workspace', 'publish-targets', project], queryFn: () => workspaceApi.publishTargets(project) })
  const gateways = gatewaysOf(targets.data ?? [])
  const [chosenGateway, setGateway] = useState('')
  const gateway = gateways.find((g) => g.name === chosenGateway) ?? gateways[0] ?? null
  const client = useMemo(() => (gateway?.instance ? createStoreClient(relayFetcher, storeBaseOf(gateway.instance)) : null), [gateway?.instance])

  if (models.length === 0) return <div className="hint" data-testid="wsp-rows-no-model">No model yet: an entity's rows are edited once the draft has a model published to a Gateway.</div>
  return (
    <div data-testid="wsp-rows">
      <div className="hint" style={{ marginBottom: 6 }}>The rows of a published model, on a Gateway that serves it, as you: what you may read and change is the Gateway's to say, and a change the model wants approved waits on the Approvals page.</div>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }}>
        <select className="input mono" aria-label="Model" value={model} onChange={(e) => setModel(e.target.value)} data-testid="wsp-rows-model">
          {models.map((m) => <option key={m} value={m}>{m}</option>)}
        </select>
        <select className="input" aria-label="Gateway" value={gateway?.name ?? ''} onChange={(e) => setGateway(e.target.value)} data-testid="wsp-rows-gateway" disabled={gateways.length === 0}>
          {gateways.length === 0 ? <option value="">No Gateway paired</option> : gateways.map((g) => <option key={g.name} value={g.name}>{g.name}</option>)}
        </select>
      </div>
      <ErrorBanner error={targets.error} />
      {targets.isSuccess && gateways.length === 0 && <div className="hint" data-testid="wsp-rows-no-gateway">No Gateway is paired with this server: an administrator pairs one under Publisher keys, and the model is published to it from the Builds tab.</div>}
      {client && <StoreClientProvider client={client}><Entities key={`${gateway!.name}/${model}`} model={model} canWrite={canWrite} /></StoreClientProvider>}
    </div>
  )
}
