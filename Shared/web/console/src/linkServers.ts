/**
 * Which servers the console can link to a gateway, and what linking them means (plan 13 T11; plan 24a UC13). A Runner pairs only to tell the gateway's result cache that data it wrote has changed (it signs as itself, with the Publisher
 * role; no row data travels).
 */
export const linkableProducts = ['dbdatarunner'] as const

export type LinkableProduct = (typeof linkableProducts)[number]

export interface LinkCandidate { id: string; name: string; product: string | null }

/** The servers that can be the first end of a link, in the order they are listed. */
export function linkableServers<T extends LinkCandidate>(instances: readonly T[]): T[] {
  return instances.filter((i) => (linkableProducts as readonly string[]).includes(i.product ?? ''))
}

export function isLinkable(product: string | null | undefined): product is LinkableProduct {
  return (linkableProducts as readonly string[]).includes(product ?? '')
}

/** One sentence of what the link lets the server do on the gateway. */
export function linkPurpose(): string {
  return 'Lets the Runner tell the gateway when a task wrote a dataset, so the gateway empties the cached answers that read it. The console makes the pairing code and hands it over; nobody types it.'
}

/** What the screen says after a link. */
export function linkDone(from: string, to: string, name: string): string {
  return `${from} now tells ${to} (“${name}”) when it writes data.`
}
