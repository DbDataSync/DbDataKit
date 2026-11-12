import { describe, expect, it } from 'vitest'
import { isLinkable, linkDone, linkPurpose, linkableServers, publishesToGateway } from './linkServers'

const servers = [
  { id: 'b', name: 'Build', product: 'dbdatabuild' },
  { id: 's', name: 'Sync', product: 'dbdatasync' },
  { id: 'r', name: 'Runner', product: 'dbdatarunner' },
  { id: 'g', name: 'Gateway', product: 'dbdatagateway' },
  { id: 'k', name: 'Store', product: 'dbdatastore' },
  { id: 'x', name: 'Unknown', product: null },
]

describe('linking a server to a gateway', () => {
  it('offers Build, Sync and the Runner as the first end and nothing else', () => {
    expect(linkableServers(servers).map((i) => i.id)).toEqual(['b', 's', 'r'])
    expect(isLinkable('dbdatasync')).toBe(true)
    expect(isLinkable('dbdatagateway')).toBe(false)
    expect(isLinkable(null)).toBe(false)
  })

  it('says what the link lets each kind of server do', () => {
    expect(linkPurpose('dbdatabuild')).toContain('publish to the gateway')
    expect(linkPurpose('dbdatasync')).toContain('wrote rows to a table')
    expect(linkPurpose('dbdatarunner')).toContain('wrote a dataset')
    expect(linkPurpose(undefined)).toContain('Build server')
  })

  it('words the result by what was linked and shows the publisher trust note only for a server that publishes', () => {
    expect(linkDone('dbdatabuild', 'Sales build', 'Edge', 'edge')).toBe('Sales build can now publish to Edge as “edge”.')
    expect(linkDone('dbdatasync', 'Sync one', 'Edge', 'edge')).toBe('Sync one now tells Edge (“edge”) when it writes data.')
    expect(publishesToGateway('dbdatabuild')).toBe(true)
    expect(publishesToGateway('dbdatarunner')).toBe(false)
  })
})
