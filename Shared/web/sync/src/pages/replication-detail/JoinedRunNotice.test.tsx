import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { JoinedRunNotice } from './JoinedRunNotice'

describe('Run now while a run is going', () => {
  it('says nothing new was started and when the run in progress began', () => {
    const since = '2026-11-14T09:30:00Z'
    const html = renderToStaticMarkup(<JoinedRunNotice since={since} />)
    expect(html).toContain('data-testid="live-run-joined"')
    expect(html).toContain('Already running since')
    expect(html).toContain(new Date(since).toLocaleString())
    expect(html).toContain('nothing new was started')
  })

  it('says nothing when the run was started by the press', () => {
    expect(renderToStaticMarkup(<JoinedRunNotice since={null} />)).toBe('')
    expect(renderToStaticMarkup(<JoinedRunNotice since={undefined} />)).toBe('')
  })
})
