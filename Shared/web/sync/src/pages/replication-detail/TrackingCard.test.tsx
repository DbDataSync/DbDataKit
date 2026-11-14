import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { TrackingCopyView, TrackingStatusView, retentionText, trackingSummary } from './TrackingCard'
import type { ReplicationTrackingStatus, TrackingCopyStatus } from '../../api/types'

const base: ReplicationTrackingStatus = {
  requestedMode: 'state', requestedPrimary: null, effectiveMode: 'state', effectivePrimary: null, defaulted: true,
  reason: 'No tracking section: the state is kept in the runner\'s state, as before.',
  targetDriver: 'MsSql', targetCanHoldState: true, targetWritersBuilt: true, copy: null, retentionEntries: 1000, retentionDays: 7,
}

describe('the tracking card status', () => {
  it('says the default is the runner\'s state and the target\'s writer is there', () => {
    const html = renderToStaticMarkup(<TrackingStatusView status={base} />)
    expect(html).toContain('state (the default)')
    expect(html).not.toContain('asked for')
    expect(html).toContain('No tracking section')
    expect(html).toContain('dbdatasync.position_log')
  })

  it('says what was asked for and why it does not apply on an engine without a writer', () => {
    const html = renderToStaticMarkup(<TrackingStatusView status={{ ...base, requestedMode: 'both', requestedPrimary: 'target', defaulted: false, targetDriver: 'MySql', targetWritersBuilt: false, reason: 'Mode \'both\' keeps the state on the target. The target (MySql): the writer is not built yet' }} />)
    expect(html).toContain('asked for')
    expect(html).toContain('both, the target primary')
    expect(html).toContain('no writer keeps the position log on MySQL, MariaDB or Oracle')
  })

  it('shows no asked-for part when the mode applies as asked', () => {
    const html = renderToStaticMarkup(<TrackingStatusView status={{ ...base, requestedMode: 'state', requestedPrimary: null, defaulted: false, reason: 'The state is kept in the runner\'s state only, as asked.' }} />)
    expect(html).not.toContain('asked for')
    expect(html).toContain('as asked')
  })

  it('says a Flight SQL or DuckDB target keeps its state on the runner', () => {
    const html = renderToStaticMarkup(<TrackingStatusView status={{ ...base, targetDriver: 'DuckDb', targetCanHoldState: false, targetWritersBuilt: false }} />)
    expect(html).toContain('cannot hold a state table')
  })

  it('names both and its primary when they apply', () => {
    expect(trackingSummary({ ...base, effectiveMode: 'both', effectivePrimary: 'state', defaulted: false })).toBe('both, the state primary')
  })
})

describe('the state\'s copy of the target\'s entries', () => {
  const copy: TrackingCopyStatus = { identity: 'syncposition:sqlserver://sql01/wh/orders@sales', rows: 40, followedAt: '2026-11-12T10:00:00Z', caughtUp: true, error: null, divergenceId: null, divergenceReason: null, divergedAt: null, divergences: 0 }
  const render = (over: Partial<TrackingCopyStatus> = {}, canResolve = true) =>
    renderToStaticMarkup(<TrackingCopyView copy={{ ...copy, ...over }} canResolve={canResolve} onResolve={() => {}} onCatchUp={() => {}} />)

  it('says how many entries it holds and that it is caught up', () => {
    const html = render()
    expect(html).toContain('40 entries')
    expect(html).toContain('caught up')
    expect(html).not.toContain('tracking-divergence')
  })

  it('shows an open divergence with its reason and both ways to resolve it', () => {
    const html = render({ divergenceId: 7, divergenceReason: 'The target was restored from a backup.' })
    expect(html).toContain('diverged, not copying')
    expect(html).toContain('The target was restored from a backup.')
    expect(html).toContain('tracking-resolve-archive')
    expect(html).toContain('tracking-resolve-reset')
    expect(html).not.toContain('An administrator resolves it.')
  })

  it('leaves the resolve to an administrator and says so', () => {
    const html = render({ divergenceId: 7, divergenceReason: 'x' }, false)
    expect(html).toContain('An administrator resolves it.')
    expect(html.match(/disabled=""/g)?.length).toBeGreaterThanOrEqual(3)   // follow now, archive, reset
  })

  it('says the last follow failed with the error, and counts resolved divergences', () => {
    const failed = render({ error: 'The target \'sales\' did not answer: timeout', caughtUp: false })
    expect(failed).toContain('the last follow failed')
    expect(failed).toContain('did not answer')
    expect(render({ divergences: 2 })).toContain('2 earlier divergences resolved.')
  })

  it('says what the retention keeps', () => {
    expect(retentionText(1000, 7)).toBe('The target\'s table keeps the newest 1000 entries of each mapping and everything newer than 7 days; the newest entry of each source is never removed.')
    expect(retentionText(1, 1)).toContain('newest 1 entry of each mapping and everything newer than 1 day;')
  })
})
