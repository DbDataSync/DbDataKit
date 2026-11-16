import { describe, expect, it } from 'vitest'
import { isLinkable, linkDone, linkPurpose, linkableServers } from './linkServers'

const servers = [
  { id: 'r', name: 'Runner', product: 'dbdatarunner' },
  { id: 'g', name: 'Gateway', product: 'dbdatagateway' },
  { id: 'w', name: 'Workspace', product: 'dbdataworkspace' },
  { id: 'b', name: 'Build', product: 'dbdatabuild' },
  { id: 'x', name: 'Unknown', product: null },
]

describe('linking a server to a gateway', () => {
  it('offers the Runner as the first end and nothing else (a paired Build or Sync server is not offered: they were deleted)', () => {
    expect(linkableServers(servers).map((i) => i.id)).toEqual(['r'])
    expect(isLinkable('dbdatarunner')).toBe(true)
    expect(isLinkable('dbdatasync')).toBe(false)
    expect(isLinkable('dbdatagateway')).toBe(false)
    expect(isLinkable(null)).toBe(false)
  })

  it('says what the link lets the Runner do, and words the result', () => {
    expect(linkPurpose()).toContain('wrote a dataset')
    expect(linkDone('Runner one', 'Edge', 'edge')).toBe('Runner one now tells Edge (“edge”) when it writes data.')
  })
})
