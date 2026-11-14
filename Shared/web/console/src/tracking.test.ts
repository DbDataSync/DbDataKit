import { afterEach, describe, expect, it, vi } from 'vitest'
import { commonText, connectionOf, copyRows, keepStale, lagText, switchPrimary, switchPrimaryOf, switchRefusal, type CopyRow, type Loaded, type TrackingDocument, type TrackingDivergence } from './tracking'
import type { InstanceSummary } from './api'

const server = (id: string, name: string, product: string): InstanceSummary => ({ id, name, url: 'https://x', product, version: '1', apiVersion: 1, reachable: true, ready: true, problem: null, pairedAt: null })
const build = server('b1', 'Build prod', 'dbdatabuild')
const identity = 'buildtracking:mssql://sql01/wh.dbdatabuild.tracking@sales'
const point = (key: string, time: string) => ({ order: 1, key, time })
const divergence = (over: Partial<TrackingDivergence> = {}): TrackingDivergence => ({ id: 1, identity, stream: 'run_log', status: 'open', open: true, reason: 'the target has fewer rows', common: point('a|1', '2026-11-10T10:00:00.0000000Z'), primary_high: point('a|2', '2026-11-10T10:05:00.0000000Z'), copy_high: point('a|9', '2026-11-10T11:00:00.0000000Z'), diverged_at: '2026-11-10T12:00:00Z', resolved_at: null, resolved_by: null, ...over })
const doc = (over: Partial<TrackingDocument> = {}): TrackingDocument => ({
  mode: 'both', primary: 'target', divergences: [],
  copies: [{ identity, streams: [{ stream: 'run_log', rows: 5, checkpoint: point('a|1', '2026-11-10T10:00:00.0000000Z'), diverged: false }, { stream: 'ddl_log', rows: 2, checkpoint: point('b|1', '2026-11-10T10:30:00.0000000Z'), diverged: false }, { stream: 'block_log', rows: 0, checkpoint: null, diverged: false }] }],
  ...over,
})

describe('a copy of a connection', () => {
  it('names the connection the identity ends with, the mode and primary, the rows and the newest copied row', () => {
    expect(connectionOf(identity)).toBe('sales')
    const [row] = copyRows(build, doc())
    expect(row).toMatchObject({ connection: 'sales', mode: 'both', primary: 'target', streams: 3, rows: 7, newest: '2026-11-10T10:30:00.0000000Z', product: 'dbdatabuild', open: [] })
  })

  it('carries the open divergences of its identity only, and says a runner is a runner', () => {
    const rows = copyRows(server('r1', 'Runner', 'dbdatarunner'), doc({ divergences: [divergence(), divergence({ id: 2, open: false, status: 'archived' }), divergence({ id: 3, identity: 'other@x' })] }))
    expect(rows[0].product).toBe('dbdatarunner')
    expect(rows[0].open.map((d) => d.id)).toEqual([1])
  })

  it('has no newest row while nothing was copied', () => {
    const [row] = copyRows(build, doc({ copies: [{ identity, streams: [{ stream: 'run_log', rows: 0, checkpoint: null, diverged: false }] }] }))
    expect(row.newest).toBeNull()
    expect(lagText(row.newest, Date.now())).toBe('Nothing copied yet')
  })
})

describe('the lag in words', () => {
  const now = Date.parse('2026-11-10T12:00:00Z')
  it('counts from the newest copied row', () => {
    expect(lagText('2026-11-10T11:59:40.0000000Z', now)).toBe('Newest row under a minute old')
    expect(lagText('2026-11-10T11:30:00.0000000Z', now)).toBe('Newest row 30 min old')
    expect(lagText('2026-11-10T06:00:00.0000000Z', now)).toBe('Newest row 6 h old')
    expect(lagText('2026-11-05T12:00:00.0000000Z', now)).toBe('Newest row 5 days old')
  })
})

describe('a divergence', () => {
  it('says where the sides part, or that they share nothing', () => {
    expect(commonText(divergence())).toContain('a|1')
    expect(commonText(divergence({ common: null }))).toContain('share no row')
  })
})

describe('switching the primary', () => {
  const [base] = copyRows(build, doc())
  const nothing: Loaded['problems'] = []

  it('is refused while the server that holds the copy is down, naming it', () => {
    expect(switchPrimary(base, [{ server: 'Build prod', message: 'No answer' }]).message).toMatch(/Build prod does not answer/)
    expect(switchPrimary({ ...base, stale: true }, nothing).message).toMatch(/Build prod does not answer/)
  })

  it('is refused while a divergence is open, naming the streams', () => {
    expect(switchPrimary({ ...base, open: [divergence()] }, nothing).message).toMatch(/open divergence \(run_log\)/)
  })

  it('is refused with one side only', () => {
    expect(switchPrimary({ ...base, mode: 'target', primary: null }, nothing).message).toMatch(/one side only/)
  })

  it('passes the checks made here when both sides are up and agree, and then asks the server', () => {
    expect(switchRefusal(base, nothing)).toBeNull()
  })

  it('is sent to the package for a runner, whose primary is the project\'s', () => {
    const [row] = copyRows(server('r1', 'Runner', 'dbdatarunner'), doc())
    expect(switchRefusal(row, nothing)).toMatch(/package it runs/)
  })

  describe('asked of a Build server', () => {
    afterEach(() => vi.unstubAllGlobals())
    const answer = (status: number, body: unknown) => vi.fn(async () => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))

    it('goes to the state when the server says it switched', async () => {
      const fetcher = answer(200, { switched: true, mode: 'both', primary: 'state' })
      vi.stubGlobal('fetch', fetcher)
      const result = await switchPrimaryOf(build, base, nothing)
      expect(result.done).toBe(true)
      expect(result.message).toMatch(/state first and to the target second/)
      const [url, init] = fetcher.mock.calls[0] as unknown as [string, RequestInit]
      expect(url).toContain('/relay/b1/api/v1/tracking/primary')
      expect(JSON.parse(String(init.body))).toEqual({ primary: 'state' })
    })

    it('goes back to the target from a state primary', async () => {
      const fetcher = answer(200, { switched: true })
      vi.stubGlobal('fetch', fetcher)
      const result = await switchPrimaryOf(build, { ...base, primary: 'state' }, nothing)
      expect(result.done).toBe(true)
      expect(JSON.parse(String((fetcher.mock.calls[0] as unknown as [string, RequestInit])[1].body))).toEqual({ primary: 'target' })
    })

    it('says what the server refused with, and changes nothing', async () => {
      vi.stubGlobal('fetch', answer(409, { switched: false, error: 'Not switched: the two sides could not be made level (the target does not answer).' }))
      const result = await switchPrimaryOf(build, base, nothing)
      expect(result).toEqual({ done: false, message: 'Not switched: the two sides could not be made level (the target does not answer).' })
    })

    it('does not ask the server while a check made here refuses', async () => {
      const fetcher = answer(200, {})
      vi.stubGlobal('fetch', fetcher)
      const result = await switchPrimaryOf(build, { ...base, open: [divergence()] }, nothing)
      expect(result.done).toBe(false)
      expect(fetcher).not.toHaveBeenCalled()
    })
  })
})

describe('a Sync server (plan 28c E17)', () => {
  const sync = server('s1', 'Sync prod', 'dbdatasync')
  const positionLogIdentity = 'syncposition:sqlserver://sql01/wh/orders@sales'
  const syncDoc = (over: Partial<TrackingDocument> = {}): TrackingDocument => ({
    holder: 'sync-server', mode: 'state', primary: null, divergences: [],
    copies: [
      { identity: positionLogIdentity, replication: 'orders', mode: 'both', primary: 'target', streams: [{ stream: 'position', rows: 40, checkpoint: { order: 40, key: 'u-40', time: '2026-11-10T10:00:00.0000000Z' }, diverged: false }] },
      { identity: 'syncposition:postgres://pg/wh/events@analytics', replication: 'events', mode: 'target', primary: null, streams: [{ stream: 'position', rows: 3, checkpoint: null, diverged: false }] },
    ],
    ...over,
  })

  it('shows a copy for each replication with its own mode and primary, named by the replication', () => {
    const rows = copyRows(sync, syncDoc())
    expect(rows.map((r) => [r.connection, r.mode, r.primary, r.product, r.rows])).toEqual([['orders', 'both', 'target', 'dbdatasync', 40], ['events', 'target', null, 'dbdatasync', 3]])
  })

  it('names the entry both sides hold even when the copy no longer holds its time', () => {
    expect(commonText(divergence({ identity: positionLogIdentity, stream: 'position', common: { order: 3, key: 'u-3', time: null } }))).toBe('The last row both hold is u-3.')
  })

  it('sends the person to the replication to change the primary, and still refuses first for a down server or an open divergence', () => {
    const [row] = copyRows(sync, syncDoc())
    expect(switchPrimary(row, []).message).toMatch(/Overview tab, Tracking card/)
    expect(switchPrimary({ ...row, stale: true }, []).message).toMatch(/Sync prod does not answer/)
    expect(switchPrimary({ ...row, open: [divergence({ stream: 'position' })] }, []).message).toMatch(/open divergence/)
  })
})

describe('a server that stops answering', () => {
  it('keeps its copies on the page, marked stale, and leaves the others as they are', () => {
    const rows: CopyRow[] = [...copyRows(build, doc()), ...copyRows(server('r1', 'Runner', 'dbdatarunner'), doc())]
    const kept = keepStale(rows, { rows: [rows[1]], divergences: [], problems: [{ server: 'Build prod', message: 'No answer' }] })
    expect(kept.rows.map((r) => [r.serverName, r.stale ?? false])).toEqual([['Runner', false], ['Build prod', true]])
  })
})
