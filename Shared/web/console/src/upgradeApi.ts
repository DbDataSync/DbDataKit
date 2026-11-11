import { request } from '@dbdatakit/shell'

/** A release the feed lists (plan 24a UC08). */
export interface AvailableRelease {
  version: string
  channel: string
  published: string | null
  notesUrl: string | null
  /** The compatibility generation the release was built with, when it says (only the newest of a product is read). */
  generation: number | null
}

export interface ProductReleases {
  product: string
  releases: AvailableRelease[]
  newest: string | null
  newestGeneration: number | null
}

/** One paired server against the feed: what it runs, the newest release of its product, and the mark for a release outside N and N-1 of this console. */
export interface ServerAvailability {
  id: string
  name: string
  product: string | null
  version: string | null
  generation: number | null
  newest: string | null
  newerAvailable: boolean
  newestGeneration: number | null
  notesUrl: string | null
  /** Set when the newest release would leave the pair outside N and N-1: the words, naming both versions and generations and the side to upgrade. */
  outsideAfterRelease: string | null
}

export interface AvailableUpgrades {
  feed: string
  channel: string
  mirror: boolean
  at: string
  consoleGeneration: number
  consoleVersion: string
  products: ProductReleases[]
  servers: ServerAvailability[]
}

export const availableKey = ['upgrades', 'available'] as const

export const upgradeApi = {
  available: (refresh = false) => request<AvailableUpgrades>(`/api/upgrades/available${refresh ? '?refresh=true' : ''}`),
}

/** "9.9.9 to 2026.11.9.1430", or what the row says when there is nothing newer. */
export function newestLine(row: ServerAvailability): string {
  if (row.newest === null) return 'No release in the feed'
  return row.newerAvailable ? `Newer available: ${row.newest}` : 'Up to date'
}
