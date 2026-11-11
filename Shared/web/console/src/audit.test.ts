import { describe, expect, it } from 'vitest'
import { appendPage, auditQuery, dayBound, noFilter, outcomeLabel, relayedPath, what, who, type AuditEntry } from './audit'

const entry = (over: Partial<AuditEntry> = {}): AuditEntry => ({ id: 1, at: '2026-11-11T10:00:00Z', actor: 'u1', actorName: 'Ada', verb: 'relay.write', outcome: 'ok', targetKind: 'server', targetId: 'st1', targetName: null, detail: null, ...over })

describe('the audit query', () => {
  it('asks for a page of fifty with nothing narrowed', () => {
    expect(auditQuery(noFilter)).toBe('?limit=50')
  })

  it('carries each field that narrows and the id the page starts after', () => {
    const q = new URLSearchParams(auditQuery({ verb: 'server.', outcome: 'refused', actor: ' u1 ', from: '', to: '' }, 120, 25))
    expect(Object.fromEntries(q)).toEqual({ verb: 'server.', outcome: 'refused', actor: 'u1', beforeId: '120', limit: '25' })
  })

  it('turns a date into the start of that local day, and the end of one into the start of the next', () => {
    expect(dayBound('2026-11-11')).toBe(new Date(2026, 10, 11).toISOString())
    expect(dayBound('2026-11-11', true)).toBe(new Date(2026, 10, 12).toISOString())
    expect(dayBound('2026-12-31', true)).toBe(new Date(2027, 0, 1).toISOString())
    expect(dayBound('yesterday')).toBe('')
    expect(auditQuery({ ...noFilter, from: '2026-11-01', to: 'nonsense' })).toContain('from=')
    expect(auditQuery({ ...noFilter, from: '2026-11-01', to: 'nonsense' })).not.toContain('to=')
  })
})

describe('a line of the audit', () => {
  it('says who by name, falls back to the id, and says nobody for a call nobody signed in for', () => {
    expect(who(entry())).toBe('Ada')
    expect(who(entry({ actorName: null }))).toBe('u1')
    expect(who(entry({ actor: 'anonymous', actorName: null }))).toBe('Nobody signed in')
  })

  it('says a relayed write with its method, the path on the server and the server\'s name', () => {
    expect(what(entry({ detail: { method: 'PUT', path: '/relay/st1/api/v1/models/budgets', serverName: 'Master data' } }))).toBe('PUT /api/v1/models/budgets on Master data')
    expect(relayedPath('/relay/st1/api/x')).toBe('/api/x')
    expect(relayedPath(undefined)).toBe('/')
  })

  it('says the server verbs with the name the server had, and the user verbs with the person\'s name', () => {
    expect(what(entry({ verb: 'server.forget', detail: { serverName: 'Build prod' } }))).toBe('removed the server Build prod')
    expect(what(entry({ verb: 'server.confirm', detail: null }))).toBe('confirmed the waiting server st1')
    expect(what(entry({ verb: 'user.update', targetKind: 'user', targetId: 'u9', targetName: 'Bo' }))).toBe('changed the roles or details of Bo')
    expect(what(entry({ verb: 'invite.redeem', detail: { name: 'Cy' } }))).toBe('redeemed an invitation as Cy')
  })

  it('shows a verb it does not know as it is', () => {
    expect(what(entry({ verb: 'thing.happened' }))).toBe('thing.happened')
  })

  it('names the outcomes as a person says them', () => {
    expect([outcomeLabel('ok'), outcomeLabel('refused'), outcomeLabel('failed'), outcomeLabel('odd')]).toEqual(['Done', 'Refused', 'Failed', 'odd'])
  })

  it('adds a next page without showing an entry twice', () => {
    expect(appendPage([entry({ id: 3 }), entry({ id: 2 })], [entry({ id: 2 }), entry({ id: 1 })]).map((e) => e.id)).toEqual([3, 2, 1])
  })
})
