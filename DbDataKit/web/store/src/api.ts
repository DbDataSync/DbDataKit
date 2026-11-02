import { apiUrl, request } from '@dbdatakit/shell'
import { createStoreClient, type Fetcher } from '@dbdatakit/store-ui'

export interface ModelSummary { name: string; title: string | null; valid: boolean; entities: string[] }

/** The Store's calls through the console's relay, as the signed-in person. The same controls an app mounts get a fetcher with the app's own token instead. */
export const relayFetcher: Fetcher = (path, init) => fetch(apiUrl(path), { ...init, credentials: 'same-origin' })

export const storeClient = createStoreClient(relayFetcher)

export const storeApi = {
  models: () => request<{ models: ModelSummary[] }>('/api/v1/models').then((r) => r.models),
}
