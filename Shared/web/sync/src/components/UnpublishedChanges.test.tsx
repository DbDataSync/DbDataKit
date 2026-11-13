import { MemoryRouter } from 'react-router-dom'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { isUnpublished, UnpublishedNotice, UnpublishedTag } from './UnpublishedChanges'
import type { PublishState } from '../api/types'

const state = (patch: Partial<PublishState> = {}): PublishState => ({
  everPublished: true, unpublished: true, sharedChanged: false, replications: ['orders'], publishedAtUtc: '2026-11-13T05:00:00Z', version: 7, mode: 'stage', ...patch,
})

describe('the unpublished-changes indicator', () => {
  it('names the replications that changed and the shared files that touch all of them', () => {
    expect(isUnpublished(state(), 'orders')).toBe(true)
    expect(isUnpublished(state(), 'events')).toBe(false)
    expect(isUnpublished(state({ sharedChanged: true, replications: [] }), 'events')).toBe(true)
  })

  it('says nothing for a repository never published, or published and unchanged', () => {
    expect(isUnpublished(state({ everPublished: false, unpublished: false, replications: [] }), 'orders')).toBe(false)
    expect(isUnpublished(state({ unpublished: false, replications: [] }), 'orders')).toBe(false)
    expect(isUnpublished(undefined, 'orders')).toBe(false)
    expect(renderToStaticMarkup(<UnpublishedTag state={null} replication="orders" />)).toBe('')
  })

  it('says the runner runs the published version until the next publish, with the version and where to publish', () => {
    const html = renderToStaticMarkup(<MemoryRouter><UnpublishedNotice state={state()} replication="orders" /></MemoryRouter>)
    expect(html).toContain('data-testid="unpublished-changes"')
    expect(html).toContain('version 7')
    expect(html).toContain('until the replication set is published again')
    expect(html).toContain('href="/runner"')
  })

  it('explains a change of the shared files in its own words', () => {
    const html = renderToStaticMarkup(<MemoryRouter><UnpublishedNotice state={state({ sharedChanged: true, replications: [] })} replication="events" /></MemoryRouter>)
    expect(html).toContain('connections, libraries or drivers')
  })
})
