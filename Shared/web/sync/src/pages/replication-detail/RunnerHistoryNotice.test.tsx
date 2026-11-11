import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { RunnerHistoryNotice } from './RunnerHistoryNotice'

describe('the notice of a runner that could not be asked', () => {
  it('says the history is incomplete and why', () => {
    const html = renderToStaticMarkup(<RunnerHistoryNotice unavailable="https://runner:8495 could not be reached (connection refused)." />)
    expect(html).toContain('data-testid="runner-history-unavailable"')
    expect(html).toContain('incomplete')
    expect(html).toContain('could not be reached')
  })

  it('says nothing when the runner answered or does not own the replication', () => {
    expect(renderToStaticMarkup(<RunnerHistoryNotice unavailable={null} />)).toBe('')
    expect(renderToStaticMarkup(<RunnerHistoryNotice unavailable={undefined} />)).toBe('')
    expect(renderToStaticMarkup(<RunnerHistoryNotice unavailable="" />)).toBe('')
  })
})
