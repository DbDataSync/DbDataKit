/** The result cache of one Gateway instance (plan 24a UC16): counts, the cache setting of each definition and what it heard of data changes. */
export interface CacheStats { hits: number; misses: number; entries: number; bytes: number; evictions: number; cleared: number; hit_rate: number | null }
export interface CacheDatasetRow { name: string; entries: number; bytes: number; hits: number; misses: number; hit_rate: number | null }
export interface CacheSettingRow {
  name: string
  kind: 'data-source' | 'semantic-model'
  enabled: boolean
  /** Enabled and not held back (a pass-through data source, a writable one or an Analysis Services model is never cached); `note` says why. */
  active: boolean
  ttl_seconds: number
  scope: 'caller' | 'rules' | 'shared'
  version: string | null
  datasets: string[]
  note: string | null
  entries: number
}
export interface CacheEvents {
  received: number
  /** Changes heard about a dataset this Gateway does not serve (the producer sends `only_if_served`): nothing was cleared. Absent on a Gateway before the producers existed. */
  ignored?: number
  cleared: number
  last_at: string | null
  last_product: string | null
  last_dataset: string | null
  manual: number
  last_manual_at: string | null
  /** The group shares clears through its state (more than one instance of a group hears every change). */
  shared: boolean
  recent: { dataset: string; at: string; product: string; reason: string }[]
}
export interface CacheOverview { instance: string; enabled: boolean; max_bytes: number; max_entry_bytes: number; stats: CacheStats; datasets: CacheDatasetRow[]; settings: CacheSettingRow[]; events: CacheEvents }
