import { useQuery } from '@tanstack/react-query'
import { Route, Routes, useParams } from 'react-router-dom'
import { ErrorBanner, Link, SubTabs } from '@dbdatakit/shell'
import { storeApi } from '../api'
import { StoreShell } from '../StoreShell'
import { ModelEditor } from './ModelEditor'

function DefinitionTab({ model }: { model: string }) {
  const doc = useQuery({ queryKey: ['store', 'model', model], queryFn: () => storeApi.model(model), staleTime: Infinity, refetchOnWindowFocus: false })
  if (doc.isLoading) return <div className="empty" data-testid="model-loading">Loading...</div>
  if (doc.error || !doc.data) return <ErrorBanner error={doc.error ?? new Error(`There is no model '${model}'.`)} />
  // the editor keeps its own text and baseline once it has read the model (a save or a reload moves them); a new read of the same model does not replace what a person is typing
  return <ModelEditor key={model} existing={doc.data} canWriteNew startText="" />
}

/** One model: its definition (form and YAML), as the first tab of routes (`/model/<name>`; storage and change requests are the next slices' tabs). */
export function ModelWorkspace() {
  const { model = '' } = useParams()
  const base = `/model/${encodeURIComponent(model)}`
  const entities = useQuery({ queryKey: ['store', 'models'], queryFn: storeApi.models })
  const mine = entities.data?.find((m) => m.name === model)
  return (
    <StoreShell crumbs={[{ label: 'Models', to: '/' }, { label: model, mono: true, heading: true }]}>
      <div className="pane" data-testid="store-model-workspace">
        <div className="page-head"><h1 className="page-title mono">{model}</h1></div>
        {mine && mine.entities.length > 0 && (
          <p className="hint" data-testid="model-entities">Rows: {mine.entities.map((e, i) => <span key={e}>{i > 0 ? ', ' : ''}<EntityLink model={model} entity={e} /></span>)}</p>
        )}
        <SubTabs base={base} testId="model-tabs" tabs={[
          { path: null, label: 'Definition', testId: 'model-tab-definition' },
        ]} />
        <Routes>
          <Route index element={<DefinitionTab model={model} />} />
        </Routes>
      </div>
    </StoreShell>
  )
}

function EntityLink({ model, entity }: { model: string; entity: string }) {
  return <Link to={`/models/${encodeURIComponent(model)}/${encodeURIComponent(entity)}`} data-testid="model-entity-link">{entity}</Link>
}
