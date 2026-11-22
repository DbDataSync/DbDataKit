import type { ReactElement } from 'react'
import { describe, expect, it } from 'vitest'
import { groupTestId, isEntryActive, startsCollapsed, toGroups, visibleGroups, type RailItem } from '@dbdatakit/shell'
import type { InstanceSummary } from './api'
import { consoleNav, navWithServers } from './ConsoleShell'

const titles = (groups = consoleNav) => groups.map((g) => [g.title ?? '', g.items.map((i) => i.title)])
const entry = (title: string): RailItem => consoleNav.flatMap((g) => g.items).find((i) => i.title === title)!

describe('the console navigation', () => {
  it('has Home first, the six groups of DbDataKit/README.md in order, and Docs last', () => {
    expect(titles()).toEqual([
      ['', ['Home']],
      ['Explore', ['Views', 'Lineage', 'Quality']],
      ['Build and release', ['Releases', 'Deployments', 'Backlog']],
      ['Operate', ['Monitoring', 'Runs', 'Approvals', 'Delivery', 'Tracking']],
      ['Agents and knowledge', ['Agents', 'Knowledge']],
      ['Servers', ['All servers', 'Add a server']],
      ['Settings', ['Connections', 'Bindings', 'Secrets', 'Users', 'Audit', 'Setup']],
      ['', ['Docs']],
    ])
    expect(consoleNav.map(groupTestId)).toEqual([undefined, 'nav-group-explore', 'nav-group-build-and-release', 'nav-group-operate', 'nav-group-agents-and-knowledge', 'nav-group-servers', 'nav-group-settings', undefined])
  })

  it('gives every entry a rail test id and an icon of its own', () => {
    const items = consoleNav.flatMap((g) => g.items)
    expect(items.every((i) => i.testId?.startsWith('rail-'))).toBe(true)
    expect(new Set(items.map((i) => i.testId)).size).toBe(items.length)
    expect(new Set(items.map((i) => (i.icon as ReactElement).type)).size).toBe(items.length)
  })

  it('shows an administrator every entry', () => {
    expect(visibleGroups(consoleNav, true)).toEqual(consoleNav)
  })

  it('hides the administrators\' entries from a viewer, and the Servers group, which has nothing else', () => {
    const seen = visibleGroups(consoleNav, false)
    expect(seen.flatMap((g) => g.items).some((i) => i.adminOnly)).toBe(false)
    expect(titles(seen)).toEqual([
      ['', ['Home']],
      ['Explore', ['Views', 'Lineage', 'Quality']],
      ['Build and release', ['Releases', 'Deployments', 'Backlog']],
      ['Operate', ['Monitoring', 'Runs', 'Approvals', 'Delivery', 'Tracking']],
      ['Agents and knowledge', ['Agents', 'Knowledge']],
      ['Settings', ['Connections', 'Bindings']],
      ['', ['Docs']],
    ])
  })

  it('lights a section anywhere inside it, and Upgrades, a server\'s state and a server\'s screens under All servers', () => {
    expect(isEntryActive('/views/dashboards/sales', entry('Views'))).toBe(true)
    expect(isEntryActive('/viewsx', entry('Views'))).toBe(false)
    expect(isEntryActive('/upgrades', entry('All servers'))).toBe(true)
    expect(isEntryActive('/state/runner-a', entry('All servers'))).toBe(true)
        expect(isEntryActive('/instances', entry('All servers'))).toBe(true)
    expect(isEntryActive('/', entry('Home'))).toBe(true)
    expect(isEntryActive('/runs', entry('Home'))).toBe(false)
    const lit = (path: string) => consoleNav.flatMap((g) => g.items).filter((i) => isEntryActive(path, i)).map((i) => i.title)
    for (const item of consoleNav.flatMap((g) => g.items)) expect(lit(item.to!)).toEqual([item.title])
  })

  it('starts collapsed below 1000 pixels wide whatever was kept, and as the person left it above', () => {
    expect(startsCollapsed(null, 800)).toBe(true)
    expect(startsCollapsed('false', 999)).toBe(true)
    expect(startsCollapsed(null, 1000)).toBe(false)
    expect(startsCollapsed(null, 1440)).toBe(false)
    expect(startsCollapsed('true', 1440)).toBe(true)
    expect(startsCollapsed('false', 1440)).toBe(false)
  })

  it('takes a flat list of entries (a server\'s own) as one untitled group', () => {
    const flat: RailItem[] = [{ to: '/', title: 'Tasks', icon: null }, { to: '/runs', title: 'Runs', icon: null, adminOnly: true }]
    expect(toGroups(flat)).toEqual([{ items: flat }])
    expect(toGroups(consoleNav)).toBe(consoleNav)
    expect(visibleGroups(toGroups(flat), false)).toEqual([{ items: [flat[0]] }])
  })

  const server = (id: string, product: string, reachable = true): InstanceSummary => ({ id, name: id, url: `https://${id}`, product, version: '1', apiVersion: 1, reachable, ready: reachable, problem: reachable ? null : 'connection refused', pairedAt: null })

  it('lists each paired server by its name and product under Servers, marked when it does not answer, and Projects for the Workspace', () => {
    const nav = navWithServers([server('runner-a', 'dbdatarunner'), server('gateway-east', 'dbdatagateway'), server('gateway-west', 'dbdatagateway', false), server('workspace', 'dbdataworkspace')])
    const servers = nav.find((g) => g.title === 'Servers')!.items
    expect(servers.map((i) => [i.title, i.note, i.to, i.serverId])).toEqual([
      ['All servers', undefined, '/instances', undefined],
      ['Add a server', undefined, '/add-server', undefined],
      ['gateway-east', 'Gateway', '/servers/gateway-east', 'gateway-east'],
      ['gateway-west', 'not answering', '/servers/gateway-west', 'gateway-west'],
      ['runner-a', 'Runner', '/servers/runner-a', 'runner-a'],
      ['workspace', 'Workspace', '/servers/workspace', 'workspace'],
    ])
    expect(servers[3].problem).toBe('not answering: connection refused')
    expect(nav.find((g) => g.title === 'Build and release')!.items.map((i) => [i.title, i.testId, i.to])).toEqual([
      ['Projects', 'rail-projects', '/servers/workspace'], ['Releases', 'rail-releases', '/releases'], ['Deployments', 'rail-deployments', '/deployments'], ['Backlog', 'rail-backlog', '/backlog'],
    ])
    // a viewer sees the servers, not All servers or Add a server
    expect(visibleGroups(nav, false).find((g) => g.title === 'Servers')!.items.map((i) => i.title)).toEqual(['gateway-east', 'gateway-west', 'runner-a', 'workspace'])
  })

  it('names each Workspace\'s Projects when there are several, and lights a server\'s entry anywhere in its screens', () => {
    const nav = navWithServers([server('ws-a', 'dbdataworkspace'), server('ws-b', 'dbdataworkspace')])
    expect(nav.find((g) => g.title === 'Build and release')!.items.slice(0, 2).map((i) => [i.title, i.testId])).toEqual([['Projects on ws-a', 'rail-projects-ws-a'], ['Projects on ws-b', 'rail-projects-ws-b']])
    const wsA = nav.find((g) => g.title === 'Servers')!.items[2]
    expect(isEntryActive('/servers/ws-a/sales/edit-1', wsA)).toBe(true)
    expect(isEntryActive('/servers/ws-ab', wsA)).toBe(false)
    expect(navWithServers([])).toEqual(consoleNav)
  })
})
