import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { UnpublishedNotice, unpublishedOf, type PublishState } from './UnpublishedChanges'

const state = (patch: Partial<PublishState> = {}): PublishState => ({
  published: { build: 'b7', version: '7', channel: 'dev', runner: 'runners-eu', mode: 'stage' }, commitsSince: 0, unsaved: [], ...patch,
})

describe('the unpublished-changes indicator', () => {
  it('counts every commit after the build for every replication: the set is one package', () => {
    expect(unpublishedOf(state({ commitsSince: 2 }), 'orders')).toEqual({ commits: 2, unsavedHere: 0, unsavedShared: 0 })
    expect(unpublishedOf(state({ commitsSince: 2 }), 'events')).toEqual({ commits: 2, unsavedHere: 0, unsavedShared: 0 })
  })

  it('counts the unsaved edits of this replication and of the files every replication uses, not another replication\'s', () => {
    const unsaved = ['config/replications/orders/task.yaml', 'config/replications/orders/table-mappings/dbo.a.yaml', 'config/connections/erp.yaml', 'catalog-cache.yaml', 'config/replications/events/task.yaml']
    expect(unpublishedOf(state({ unsaved }), 'orders')).toEqual({ commits: 0, unsavedHere: 2, unsavedShared: 2 })
    expect(unpublishedOf(state({ unsaved: ['config/replications/events/task.yaml'] }), 'orders')).toBeNull()
  })

  it('says nothing for a draft never published, or published with nothing after it', () => {
    expect(unpublishedOf(state({ published: null, commitsSince: 3 }), 'orders')).toBeNull()
    expect(unpublishedOf(state(), 'orders')).toBeNull()
    expect(unpublishedOf(undefined, 'orders')).toBeNull()
    expect(renderToStaticMarkup(<UnpublishedNotice state={state()} replication="orders" />)).toBe('')
  })

  it('names the version, the channel and the Runner, says the Runner runs it until the set is published again, and holds the action given', () => {
    const html = renderToStaticMarkup(
      <UnpublishedNotice state={state({ commitsSince: 1, unsaved: ['config/replications/orders/task.yaml'] })} replication="orders"><a href="/builds">Build and publish…</a></UnpublishedNotice>)
    expect(html).toContain('data-testid="unpublished-changes"')
    expect(html).toContain('Version 7 on dev of runners-eu')
    expect(html).toContain('1 commit of the draft after the build that was staged')
    expect(html).toContain('unsaved edits of 1 file of this replication')
    expect(html).toContain('until the set is built and published again')
    expect(html).toContain('a build takes only what is saved and committed')
    expect(html).toContain('href="/builds"')
  })
})
