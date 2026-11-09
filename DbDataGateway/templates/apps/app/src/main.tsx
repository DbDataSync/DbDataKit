import '@dbdatakit/store-ui/styles.css'
import '@dbdatakit/visuals/styles.css'
import './app.css'
import { EntityEditor, StoreClientProvider } from '@dbdatakit/store-ui'
import { createRoot } from 'react-dom/client'
import { store } from './clients'

// The Store's own controls over the entity the manifest lists: a grid, a form made from the model, history. The server decides what a person may do and says why when it refuses.
createRoot(document.getElementById('root')!).render(
  <StoreClientProvider client={store}>
    <main className="app">
      <h1>Territories</h1>
      <EntityEditor model="territories" entity="territory" />
    </main>
  </StoreClientProvider>,
)
