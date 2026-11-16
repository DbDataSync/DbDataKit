import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { PublishTarget } from '../api'
import { StoreRowsPanel, gatewaysOf, storeBaseOf } from './StoreRowsPanel'

const target = (over: Partial<PublishTarget>): PublishTarget => ({ name: 'edge', gateway: 'https://gw:5443', product: 'gateway', instance: 'gw-1', accepts: null, can_publish: true, ...over })

describe('the rows of a Store model on a Gateway (plan 33 SC24)', () => {
  it('offers the paired Gateways that name their instance, never a Runner', () => {
    expect(gatewaysOf([target({}), target({ name: 'jobs', product: 'runner', instance: 'r-1' }), target({ name: 'old', instance: null })]).map((t) => t.name)).toEqual(['edge'])
  })
  it('sends the controls through the console relay to the Gateway\'s store paths', () => {
    expect(storeBaseOf('gw-1')).toBe('/relay/gw-1/api/v1/store')
    expect(storeBaseOf('a b')).toBe('/relay/a%20b/api/v1/store')
  })
  it('says a draft with no model has no rows, and shows the pickers for one with a model', () => {
    const panel = (paths: string[]) => renderToStaticMarkup(<QueryClientProvider client={new QueryClient()}><StoreRowsPanel project="p" paths={paths} canWrite /></QueryClientProvider>)
    expect(panel(['README.md'])).toContain('data-testid="wsp-rows-no-model"')
    const html = panel(['models/territories.yaml'])
    expect(html).toContain('data-testid="wsp-rows-model"')
    expect(html).toContain('data-testid="wsp-rows-gateway"')
  })
})
