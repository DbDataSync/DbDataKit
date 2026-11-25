import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { ChannelList } from '../catalogRefresh'
import { ChannelPicker, RefreshDifference } from './CatalogRefresh'

const list: ChannelList = {
  package: 'crm-to-dw', connection: 'crm', slot: 'crm', runner: 'r-1', runner_name: 'runners-eu', default: 'dev',
  channels: [
    { channel: 'dev', production: false, bound: true, bound_to: 'crm_dev' },
    { channel: 'prod', production: true, bound: true, bound_to: 'crm' },
    { channel: 'test', production: false, bound: false, bound_to: null },
  ],
}
const runners = [{ id: 'r-1', name: 'runners-eu' }]
const picker = (channel: string, l: ChannelList = list) =>
  renderToStaticMarkup(<ChannelPicker runners={runners} runner="r-1" onRunner={() => {}} list={l} channel={channel} onChannel={() => {}} connection="crm" />)

describe('the channel picker', () => {
  it('offers the channels that bind the slot and names the one that does not with how to bind it', () => {
    const html = picker('dev')
    expect(html).toContain('data-channel="dev"')
    expect(html).toContain('dev: crm is crm_dev')
    expect(html).toContain('prod (production): crm is crm')
    expect(html).toMatch(/<option[^>]*disabled=""[^>]*value="test"|<option[^>]*value="test"[^>]*disabled=""/)
    expect(html).toContain('test does not bind the slot crm. Set up for development binds it with a login you type, or an administrator binds it on the Runner&#x27;s Packages page (Bindings)')
    expect(html).not.toContain('wsp-channel-production')
  })

  it('warns when production is picked, and the read is not blocked by it', () => {
    const html = picker('prod')
    expect(html).toContain('wsp-channel-production')
    expect(html).toContain('This reads production: the shape of crm as the prod channel of runners-eu binds it')
    expect(html).not.toContain('disabled=""><option')
  })

  it('says how to bind a slot when no channel binds it', () => {
    const html = picker('', { ...list, default: null, channels: [], note: 'runners-eu has no package crm-to-dw yet. Publish a build of the draft to a channel of runners-eu (staged is enough), then an administrator binds the slot.' })
    expect(html).toContain('wsp-channel-note')
    expect(html).toContain('has no package crm-to-dw yet')
  })
})

describe('the difference of a refresh', () => {
  it('lists each change before anything is replaced, with Accept and Dismiss', () => {
    const html = renderToStaticMarkup(<RefreshDifference canEdit onAccept={() => {}} onDismiss={() => {}}
      difference={{ tablesAdded: [], tablesRemoved: [], columnsAdded: [{ table: 'crm.dbo.accounts', column: 'segment', to: 'nvarchar(20)' }], columnsRemoved: [], columnsRetyped: [] }} />)
    expect(html).toContain('1 column added')
    expect(html).toContain('data-change="column-added"')
    expect(html).toContain('crm.dbo.accounts.segment')
    expect(html).toContain('Accept and replace the cache')
    expect(html).toContain('wsp-refresh-dismiss')
  })

  it('says nothing changed and still lets the new read be recorded', () => {
    const html = renderToStaticMarkup(<RefreshDifference canEdit onAccept={() => {}} onDismiss={() => {}} difference={{ tablesAdded: [], tablesRemoved: [], columnsAdded: [], columnsRemoved: [], columnsRetyped: [] }} />)
    expect(html).toContain('No change since the cache was read.')
    expect(html).toContain('Record the new read')
  })
})
