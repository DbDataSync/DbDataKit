import { describe, expect, it } from 'vitest'
import { visibleGroups } from '@dbdatakit/shell'
import { gatewayNav } from './GatewayShell'

const titles = (groups = gatewayNav) => groups.map((g) => [g.title ?? '', g.items.map((i) => i.title)])

describe('the Gateway\'s navigation', () => {
  it('has Overview, then Serving, Access and Server', () => {
    expect(titles()).toEqual([
      ['', ['Overview']],
      ['Serving', ['Data sources', 'Models', 'Saved queries', 'Apps', 'Result cache']],
      ['Access', ['Access rules', 'Users and sign-in', 'Identity home', 'Publishers', 'Secrets']],
      ['Server', ['Certificate', 'Activity', 'Server settings', 'Configuration history']],
    ])
  })

  it('hides from a viewer the pages that answer "for administrators", and the Server group, which has nothing else', () => {
    expect(titles(visibleGroups(gatewayNav, false))).toEqual([
      ['', ['Overview']],
      ['Serving', ['Models', 'Saved queries', 'Apps', 'Result cache']],
      ['Access', ['Access rules', 'Publishers']],
    ])
  })
})
