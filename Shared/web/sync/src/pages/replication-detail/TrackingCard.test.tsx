import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { TrackingStatusView, trackingSummary } from './TrackingCard'
import type { ReplicationTrackingStatus } from '../../api/types'

const base: ReplicationTrackingStatus = {
  requestedMode: 'both', requestedPrimary: 'target', effectiveMode: 'state', effectivePrimary: null, defaulted: true,
  reason: 'The default is both with the target primary, but no writer keeps the state on the target yet (plan 28c E17), so the state is kept in the runner\'s state only.',
  targetDriver: 'MsSql', targetCanHoldState: true, targetWritersBuilt: false,
}

describe('the tracking card status', () => {
  it('says the default applied as state and why', () => {
    const html = renderToStaticMarkup(<TrackingStatusView status={base} />)
    expect(html).toContain('state (the default)')
    expect(html).toContain('asked for')
    expect(html).toContain('both, the target primary')
    expect(html).toContain('no writer keeps the state on the target yet')
    expect(html).toContain('can hold a state table once its writer keeps one')
  })

  it('shows no asked-for part when the mode applies as asked', () => {
    const html = renderToStaticMarkup(<TrackingStatusView status={{ ...base, requestedMode: 'state', requestedPrimary: null, defaulted: false, reason: 'The state is kept in the runner\'s state only, as asked.' }} />)
    expect(html).not.toContain('asked for')
    expect(html).toContain('as asked')
  })

  it('says a Flight SQL or DuckDB target keeps its state on the runner', () => {
    const html = renderToStaticMarkup(<TrackingStatusView status={{ ...base, targetDriver: 'DuckDb', targetCanHoldState: false }} />)
    expect(html).toContain('cannot hold a state table')
  })

  it('names both and its primary when they apply', () => {
    expect(trackingSummary({ ...base, effectiveMode: 'both', effectivePrimary: 'state', defaulted: false })).toBe('both, the state primary')
  })
})
