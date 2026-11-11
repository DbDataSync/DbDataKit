import { useParams, useSearchParams } from 'react-router-dom'
import { Link, useIsAdmin } from '@dbdatakit/shell'
import { StoreClientProvider, EntityEditor } from '@dbdatakit/store-ui'
import { storeClient } from '../api'
import { StoreShell } from '../StoreShell'

/** The rows of one entity, with the form to change them: the reusable controls (`@dbdatakit/store-ui`) in the console's frame. */
export function EntityPage() {
  const { model = '', entity = '' } = useParams()
  const [search, setSearch] = useSearchParams()
  const instance = search.get('instance')
  const canWrite = useIsAdmin()
  return (
    <StoreShell crumbs={[{ label: 'Models', to: '/' }, { label: model, mono: true, to: `/model/${encodeURIComponent(model)}` }, { label: instance ? `${entity} (${instance})` : entity, mono: true, heading: true }]}>
      <div className="pane" data-testid="store-entity">
        <StoreClientProvider client={storeClient}>
          <EntityEditor key={`${model}/${entity}`} model={model} entity={entity} canWrite={canWrite} waitingLink={() => <Link to={`/model/${encodeURIComponent(model)}/changes`} data-testid="sui-waiting-link">Open the change requests</Link>} instance={instance} onInstance={(next) => setSearch(next ? { instance: next } : {}, { replace: true })} />
        </StoreClientProvider>
      </div>
    </StoreShell>
  )
}
