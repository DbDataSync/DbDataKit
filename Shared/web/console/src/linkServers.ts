/**
 * Which servers the console can link to a gateway, and what linking them means (plan 13 T11; plan 24a UC13). A Build server pairs to publish to the gateway; a Sync server and a Runner pair only to tell the gateway's result cache
 * that data they wrote has changed (the server signs as itself, with the Publisher role; no row data travels).
 */
export const linkableProducts = ['dbdatabuild', 'dbdatasync', 'dbdatarunner'] as const

export type LinkableProduct = (typeof linkableProducts)[number]

export interface LinkCandidate { id: string; name: string; product: string | null }

/** The servers that can be the first end of a link, in the order they are listed. */
export function linkableServers<T extends LinkCandidate>(instances: readonly T[]): T[] {
  return instances.filter((i) => (linkableProducts as readonly string[]).includes(i.product ?? ''))
}

export function isLinkable(product: string | null | undefined): product is LinkableProduct {
  return (linkableProducts as readonly string[]).includes(product ?? '')
}

/** One sentence of what the link lets this kind of server do on the gateway. */
export function linkPurpose(product: string | null | undefined): string {
  switch (product) {
    case 'dbdatasync': return 'Lets the Sync server tell the gateway when a run wrote rows to a table, so the gateway empties the cached answers that read it. The console makes the pairing code and hands it over; nobody types it.'
    case 'dbdatarunner': return 'Lets the Runner tell the gateway when a task wrote a dataset, so the gateway empties the cached answers that read it. The console makes the pairing code and hands it over; nobody types it.'
    default: return 'Lets the Build server publish to the gateway, and tell it when a refresh loaded a table. The console makes the pairing code and hands it over; nobody types it.'
  }
}

/** What the screen says after a link, by the product that was linked. */
export function linkDone(product: string | null | undefined, from: string, to: string, name: string): string {
  return product === 'dbdatabuild' || product == null
    ? `${from} can now publish to ${to} as “${name}”.`
    : `${from} now tells ${to} (“${name}”) when it writes data.`
}

/** Whether the high-trust note about published serving policy applies: only a server that publishes data sources has it. */
export function publishesToGateway(product: string | null | undefined): boolean {
  return product === 'dbdatabuild' || product == null
}
