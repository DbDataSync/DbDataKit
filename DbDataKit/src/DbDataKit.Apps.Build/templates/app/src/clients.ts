// The app's connection to its host and the clients built on it. A sandboxed document has an opaque origin: the shell page around it hands out a short-lived token over postMessage,
// and every data call goes to the host's route for this instance with that token. Which models and entities the app may touch is the manifest's `models`; the servers check it again.
import { appSemanticClient, appStoreClient, connectHost } from '@dbdatakit/view-app'

export const host = connectHost()

/** Master data: rows, one row, changes, the model's schema. */
export const store = appStoreClient(host)

/** Semantic queries: a request in catalog terms in, a typed result out (`semantic.query('sales', { measures: ['orders.net_revenue'], limit: 10 })`). */
export const semantic = appSemanticClient(host)
