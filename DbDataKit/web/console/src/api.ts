import { request } from '@dbdatakit/shell'

/** A server this console manages, with how it answered when it was last asked. */
export interface InstanceSummary {
  id: string
  name: string
  url: string
  product: string | null
  version: string | null
  apiVersion: number | null
  reachable: boolean
  ready: boolean | null
  problem: string | null
  pairedAt: string | null
}

export const consoleApi = {
  instances: {
    list: () => request<InstanceSummary[]>('/api/instances'),
    pair: (url: string, code: string, name: string | null) =>
      request<InstanceSummary>('/api/instances', { method: 'POST', body: JSON.stringify({ url, code, name }) }),
    forget: (id: string) => request<void>(`/api/instances/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  },
}
