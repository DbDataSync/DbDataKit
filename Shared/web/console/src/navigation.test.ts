import type { ReactElement } from 'react'
import { describe, expect, it } from 'vitest'
import { groupTestId, isEntryActive, startsCollapsed, toGroups, visibleGroups, type RailItem } from '@dbdatakit/shell'
import { consoleNav } from './ConsoleShell'

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
      ['Servers', ['All servers']],
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
    expect(isEntryActive('/servers/runner-a', entry('All servers'))).toBe(true)
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
})
