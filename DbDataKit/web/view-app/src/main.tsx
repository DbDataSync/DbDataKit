import '@dbdatakit/store-ui/styles.css'
import '@dbdatakit/visuals/styles.css'
import './styles.css'
import { createRoot } from 'react-dom/client'
import { appStoreClient, connectHost } from './data'
import { ViewApp } from './screens'
import type { ViewsDoc } from './spec'

// The page the apps host generated for an app of views: the specs are data in the page (`<script type="application/json" id="views-data">`; a sandboxed document cannot fetch them without CORS), the shell page brokers the token.
const host = connectHost()
const root = createRoot(document.getElementById('root')!)
try {
  const doc = JSON.parse(document.getElementById('views-data')?.textContent ?? '') as ViewsDoc
  root.render(<ViewApp doc={doc} client={appStoreClient(host)} host={host} />)
} catch (e) {
  root.render(<div className="sui-error" role="alert">{`The page holds no readable views: ${e instanceof Error ? e.message : String(e)}`}</div>)
}
