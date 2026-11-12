import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { TrackingStatusView, trackingSummary } from './TrackingCard'
import type { ReplicationTrackingStatus } from '../../api/types'

const base: ReplicationTrackingStatus = {
  requestedMode: 'state', requestedPrimary: null, effectiveMode: 'state', effectivePrimary: null, defaulted: true,
  reason: 'No tracking section: the state is kept in the runner\'s state, as before.',
  targetDriver: 'MsSql', targetCanHoldState: true, targetWritersBuilt: true,
}

describe('the tracking card status', () => {
  it('says the default is the runner\'s state and the target\'s writer is there', () => {
    const html = renderToStaticMarkup(<TrackingStatusView status={base} />)
    expect(html).toContain('state (the default)')
    expect(html).not.toContain('asked for')
    expect(html).toContain('No tracking section')
    expect(html).toContain('dbdatasync.unit')
  })

  it('says what was asked for and why it does not apply on an engine without a writer', () => {
    const html = renderToStaticMarkup(<TrackingStatusView status={{ ...base, requestedMode: 'both', requestedPrimary: 'target', defaulted: false, targetDriver: 'MySql', targetWritersBuilt: false, reason: 'Mode \'both\' keeps the state on the target. The target (MySql): the writer is not built yet' }} />)
    expect(html).toContain('asked for')
    expect(html).toContain('both, the target primary')
    expect(html).toContain('its writer is not built yet')
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
