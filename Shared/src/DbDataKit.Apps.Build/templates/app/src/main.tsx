import '@dbdatakit/store-ui/styles.css'
import '@dbdatakit/visuals/styles.css'
import '@dbdatakit/view-app/styles.css'
import './app.css'
import { ViewApp } from '@dbdatakit/view-app'
import { createRoot } from 'react-dom/client'
import { host, store } from './clients'
import { pages } from './pages'
import views from './views.generated.json'

// The screens are the files in views/ (view-spec/1: a grid, a form, a detail page, a dashboard over the entities of a Store model). `apps build` reads them with the host's own loader and puts
// them in the bundle as views.generated.json, so what is drawn is what `apps validate` checked. The library's components draw them with the Store controls and the visuals. A page of code
// (src/pages) sits beside them in the same menu.
createRoot(document.getElementById('root')!).render(<ViewApp doc={views} client={store} host={host} pages={pages} />)
