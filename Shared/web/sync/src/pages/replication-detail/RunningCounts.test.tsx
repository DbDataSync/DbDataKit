import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { RowsReadCell } from './RunningCounts'

describe('the rows a run read', () => {
  const started = '2026-11-14T09:30:00Z'

  it('says a running count is still growing, and since when the run is going', () => {
    const html = renderToStaticMarkup(<RowsReadCell run={{ runId: 'r1', status: 'Running', rowsRead: 1200, startedAtUtc: started }} />)
    expect(html).toContain((1200).toLocaleString())
    expect(html).toContain('data-testid="run-rows-read-running-r1"')
    expect(html).toContain('running')
    expect(html).toContain(`since ${new Date(started).toLocaleTimeString()}`)
  })

  it('shows the final count alone once the run ended', () => {
    const html = renderToStaticMarkup(<RowsReadCell run={{ runId: 'r2', status: 'Succeeded', rowsRead: 1200, startedAtUtc: started }} />)
    expect(html).toContain((1200).toLocaleString())
    expect(html).not.toContain('running')
    expect(html).not.toContain('since')
  })
})
