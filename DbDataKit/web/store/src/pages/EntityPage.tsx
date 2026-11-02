import { useParams } from 'react-router-dom'
import { StoreClientProvider, EntityEditor } from '@dbdatakit/store-ui'
import { storeClient } from '../api'
import { StoreShell } from '../StoreShell'

/** The rows of one entity, with the form to change them: the reusable controls (`@dbdatakit/store-ui`) in the console's frame. */
export function EntityPage() {
  const { model = '', entity = '' } = useParams()
  return (
    <StoreShell crumbs={[{ label: 'Models', to: '/' }, { label: model, mono: true }, { label: entity, mono: true, heading: true }]}>
      <div className="pane" data-testid="store-entity">
        <StoreClientProvider client={storeClient}>
          <EntityEditor key={`${model}/${entity}`} model={model} entity={entity} />
        </StoreClientProvider>
      </div>
    </StoreShell>
  )
}
