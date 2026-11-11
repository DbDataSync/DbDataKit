import { describe, expect, it } from 'vitest'
import { checklist, loadFacts, remaining, SETUP_ITEMS, type SetupFacts } from './setup'

const none: SetupFacts = { signIn: false, servers: false, connections: false, buildProjects: null }
const status = (facts: SetupFacts, skipped: string[] = []) => Object.fromEntries(checklist(facts, new Set(skipped)).map((r) => [r.id, r.status]))

describe('the setup checklist (plan 23a FQ21)', () => {
  it('lists the steps of the plan in order, the two that wait on plan 22a as not available', () => {
    expect(SETUP_ITEMS.map((i) => i.id)).toEqual(['sign-in', 'servers', 'connections', 'git-remotes', 'delivery-channels', 'build-project'])
    expect(status(none)).toEqual({ 'sign-in': 'todo', servers: 'todo', connections: 'todo', 'git-remotes': 'unavailable', 'delivery-channels': 'unavailable', 'build-project': 'todo' })
  })

  it('marks a step finished by what the install has, not by a click: done wins over skipped', () => {
    const rows = status({ signIn: true, servers: true, connections: false, buildProjects: 2 }, ['servers', 'connections'])
    expect(rows['sign-in']).toBe('done')
    expect(rows.servers).toBe('done')                  // skipped before the server was paired: it is done now
    expect(rows.connections).toBe('skipped')
    expect(rows['build-project']).toBe('done')
    expect(status({ ...none, buildProjects: 0 })['build-project']).toBe('todo')   // a Build server with no project has not done it
  })

  it('counts only what is still to do for the banner: a skipped step leaves it and stays on the list', () => {
    const skipped = checklist(none, new Set(['connections']))
    expect(remaining(skipped).map((r) => r.id)).toEqual(['sign-in', 'servers', 'build-project'])
    expect(skipped.find((r) => r.id === 'connections')!.status).toBe('skipped')           // still in the checklist
    expect(remaining(checklist({ signIn: true, servers: true, connections: true, buildProjects: 1 }, new Set()))).toEqual([])
  })
})

describe('loadFacts', () => {
  it('reads the people and the servers, and counts nothing for a page that does not answer', async () => {
    const user = (over: object) => ({ id: 'u', displayName: 'Ada', email: null, emailVerified: false, role: 'Admin', enabled: true, credentials: [{ id: 'c' }], ...over }) as never
    const facts = await loadFacts([], [user({})])
    expect(facts).toEqual({ signIn: true, servers: false, connections: false, buildProjects: null })
    expect((await loadFacts([], [user({ credentials: [] })])).signIn).toBe(false)           // a person with no way in is not a way in
    expect((await loadFacts([], [user({ enabled: false })])).signIn).toBe(false)
  })
})
