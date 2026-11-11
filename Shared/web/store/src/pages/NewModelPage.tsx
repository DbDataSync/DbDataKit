import { useIsAdmin } from '@dbdatakit/shell'
import { newModelText } from '../model'
import { StoreShell } from '../StoreShell'
import { ModelEditor } from './ModelEditor'

/** A new model: a valid starting text to change, a name, and the Store's check before anything is saved. */
export function NewModelPage() {
  const canWrite = useIsAdmin()
  return (
    <StoreShell crumbs={[{ label: 'Models', to: '/' }, { label: 'New model', heading: true }]}>
      <div className="pane" data-testid="store-new-model">
        <div className="page-head"><h1 className="page-title">New model</h1></div>
        <ModelEditor existing={null} canWriteNew={canWrite} startText={newModelText('')} />
      </div>
    </StoreShell>
  )
}
