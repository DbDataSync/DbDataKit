import { useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ApiError, ErrorBanner, request } from '@dbdatakit/shell'
import { EntityEditor, StoreClientProvider, createStoreClient, useSchema, type Fetcher } from '@dbdatakit/store-ui'
import { deployApi, useHostedServer, type ConsoleServer } from '../deploys'
import { modelsOf } from './StoreDeployPanel'

/** A Gateway the console manages, as the Rows tab offers it: the console's id (the relay is by that id), its name, and whether the console reaches it. */
export interface RowsGateway { id: string; name: string; reachable: boolean }

/** What one Gateway's store answered: the models it serves (none when it has no store module or the person has no role there), or why it did not answer. */
export interface GatewayModels { gateway: RowsGateway; models: string[] | null; problem: string | null }

/** The Gateways among the servers the console lists (`GET /api/instances`), by name. */
export const gatewaysOf = (servers: readonly ConsoleServer[]): RowsGateway[] =>
  servers.filter((s) => s.product === 'dbdatagateway').map((s) => ({ id: s.id, name: s.name, reachable: s.reachable })).sort((a, b) => a.name.localeCompare(b.name))

/** Where the store controls send their calls for a Gateway: its store paths on the management API, through the console's relay as the signed-in person. */
export const storeBaseOf = (instance: string): string => `/relay/${encodeURIComponent(instance)}/api/v1/store`

/**
 * Asks each Gateway, through the console's relay as the signed-in person, which models its store serves (`GET /api/v1/store/models`). A Gateway without
 * the store module (404) or where the person has no role (403) serves none to this person; one that does not answer, or that the console cannot reach,
 * is reported with the reason and never stops the others.
 */
export function servedModels(gateways: readonly RowsGateway[]): Promise<GatewayModels[]> {
  return Promise.all(gateways.map(async (gateway): Promise<GatewayModels> => {
    if (!gateway.reachable) return { gateway, models: null, problem: 'The console cannot reach it.' }
    try {
      const { models } = await request<{ models: { name: string }[] }>(`${storeBaseOf(gateway.id)}/models`)
      return { gateway, models: models.map((m) => m.name), problem: null }
    } catch (error) {
      if (error instanceof ApiError && (error.status === 403 || error.status === 404)) return { gateway, models: [], problem: null }
      return { gateway, models: null, problem: error instanceof Error && error.message ? error.message : 'No answer.' }
    }
  }))
}

/** The Gateways whose store serves the model, in the order they were asked. */
export const servingGateways = (answers: readonly GatewayModels[], model: string): RowsGateway[] =>
  answers.filter((a) => a.models?.includes(model)).map((a) => a.gateway)

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
 * The Rows tab of a Store project's workspace: an entity's rows of a published model, read and written with the shared `store-ui` controls against the
 * store of a Gateway the console manages (the group serves the model's default copy), as the signed-in person through the console's relay. The Gateways
 * are the console's list, and one is offered when its store serves the model; the Workspace server is not asked. Every rule is the Gateway's: who may
 * read and write, a change request when the model asks for approval (the console's Approvals page decides it), and the audit names the person. The model
 * must have been published to that Gateway (the Builds tab) and its tables deployed (the Deploy tab).
 */
export function StoreRowsPanel({ paths, canWrite }: { paths: readonly string[]; canWrite: boolean }) {
  const models = modelsOf(paths)
  const [chosenModel, setModel] = useState('')
  const model = chosenModel !== '' && models.includes(chosenModel) ? chosenModel : (models[0] ?? '')
  const hosted = useHostedServer()
  const servers = useQuery({ queryKey: ['console', 'servers'], queryFn: deployApi.servers, enabled: hosted !== null, staleTime: 10_000 })
  const gateways = gatewaysOf(servers.data ?? [])
  const answers = useQuery({
    queryKey: ['console', 'store-models', gateways.map((g) => `${g.id}:${g.reachable}`).join(',')],
    queryFn: () => servedModels(gateways),
    enabled: servers.isSuccess && gateways.length > 0,
  })
  const offered = servingGateways(answers.data ?? [], model)
  const problems = (answers.data ?? []).filter((a) => a.problem !== null)
  const [chosenGateway, setGateway] = useState('')
  const gateway = offered.find((g) => g.id === chosenGateway) ?? offered[0] ?? null
  const client = useMemo(() => (gateway ? createStoreClient(relayFetcher, storeBaseOf(gateway.id)) : null), [gateway?.id])

  if (models.length === 0) return <div className="hint" data-testid="wsp-rows-no-model">No model yet: an entity's rows are edited once the draft has a model published to a Gateway.</div>
  if (hosted === null) return <div className="hint" data-testid="wsp-rows-unavailable">The rows are read from a Gateway through the console: open this draft from the console's Workspace screens.</div>
  return (
    <div data-testid="wsp-rows">
      <div className="hint" style={{ marginBottom: 6 }}>The rows of a published model, on a Gateway that serves it, as you: what you may read and change is the Gateway's to say, and a change the model wants approved waits on the Approvals page.</div>
      <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }}>
        <select className="input mono" aria-label="Model" value={model} onChange={(e) => setModel(e.target.value)} data-testid="wsp-rows-model">
          {models.map((m) => <option key={m} value={m}>{m}</option>)}
        </select>
        <select className="input" aria-label="Gateway" value={gateway?.id ?? ''} onChange={(e) => setGateway(e.target.value)} data-testid="wsp-rows-gateway" disabled={offered.length === 0}>
          {offered.length === 0 ? <option value="">No Gateway serves {model}</option> : offered.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
        </select>
      </div>
      <ErrorBanner error={servers.error ?? answers.error} />
      {servers.isSuccess && gateways.length === 0 && <div className="hint" data-testid="wsp-rows-no-gateway">The console manages no Gateway: an administrator pairs one on the Servers page, and the model is published to it from the Builds tab.</div>}
      {answers.isSuccess && gateways.length > 0 && offered.length === 0 && <div className="hint" data-testid="wsp-rows-no-gateway">No Gateway the console manages serves {model} to you: publish it to one from the Builds tab and deploy its tables from the Deploy tab.</div>}
      {problems.map((p) => <div key={p.gateway.id} className="hint" data-testid="wsp-rows-gateway-problem" data-gateway={p.gateway.id}>{p.gateway.name} did not say which models it serves: {p.problem}</div>)}
      {client && gateway && <StoreClientProvider client={client}><Entities key={`${gateway.id}/${model}`} model={model} canWrite={canWrite} /></StoreClientProvider>}
    </div>
  )
}
