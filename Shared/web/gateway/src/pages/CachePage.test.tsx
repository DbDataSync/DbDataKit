import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { bytesText, eventsText, percent, settingState, ttlText } from '../cache'
import type { CacheOverview } from '../cacheTypes'
import { CacheView, type CacheViewProps } from './CachePage'

const overview = (over: Partial<CacheOverview> = {}): CacheOverview => ({
  instance: 'gw1', enabled: true, max_bytes: 64 * 1024 * 1024, max_entry_bytes: 8 * 1024 * 1024,
  stats: { hits: 2, misses: 1, entries: 1, bytes: 2048, evictions: 0, cleared: 3, hit_rate: 0.6667 },
  datasets: [{ name: 'data-source/erp', entries: 1, bytes: 2048, hits: 2, misses: 1, hit_rate: 0.6667 }],
  settings: [
    { name: 'erp', kind: 'data-source', enabled: true, active: true, ttl_seconds: 300, scope: 'shared', version: 'abc', datasets: ['data-source/erp'], note: null, entries: 1 },
    { name: 'live', kind: 'data-source', enabled: true, active: false, ttl_seconds: 300, scope: 'caller', version: 'def', datasets: ['data-source/live'], note: 'A pass-through data source is never cached.', entries: 0 },
    { name: 'hr', kind: 'data-source', enabled: false, active: false, ttl_seconds: 300, scope: 'caller', version: 'g', datasets: ['data-source/hr'], note: null, entries: 0 },
  ],
  events: { received: 0, cleared: 0, last_at: null, last_product: null, last_dataset: null, manual: 0, last_manual_at: null, shared: false, recent: [] },
  ...over,
})

const props = (over: Partial<CacheViewProps> = {}): CacheViewProps => ({ data: overview(), error: null, isAdmin: true, busy: false, confirming: null, cleared: null, onClear: () => {}, ...over })

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const html = (over?: Partial<CacheViewProps>) => renderToStaticMarkup(<MemoryRouter>{CacheView(props(over))}</MemoryRouter>)

describe('the cache words', () => {
  it('say rates, sizes and times plainly', () => {
    expect(percent(0.6667)).toBe('67 %')
    expect(percent(null)).toBe('–')
    expect(bytesText(512)).toBe('512 B')
    expect(bytesText(2048)).toBe('2.0 KB')
    expect(ttlText(90)).toBe('90 s')
    expect(ttlText(300)).toBe('5 min')
    expect(ttlText(7200)).toBe('2 h')
  })

  it('tell off, on and on-but-not-used apart, with the reason', () => {
    expect(settingState({ enabled: false, active: false, note: null })).toEqual({ label: 'Off', tone: 'off' })
    expect(settingState({ enabled: true, active: true, note: null }).tone).toBe('on')
    expect(settingState({ enabled: true, active: false, note: 'pass-through' })).toEqual({ label: 'On, not used: pass-through', tone: 'warn' })
  })

  it('say when no data change has arrived and what the last one was', () => {
    expect(eventsText(overview().events)).toContain('No data change has been reported')
    const events = { ...overview().events, received: 2, cleared: 5, last_at: '2026-11-12T10:00:00Z', last_product: 'build', last_dataset: 'sql/x' }
    expect(eventsText(events, new Date('2026-11-12T10:05:00Z'))).toBe('2 data changes reported, the last 5 min ago by build for sql/x; 5 entries cleared in all.')
  })
})

describe('the cache screen', () => {
  it('shows the counts, the hit rate and the settings of each definition', () => {
    const text = html()
    expect(text).toContain('67 %')
    expect(find(CacheView(props()), 'cache-setting-state-erp')!.props['data-tone']).toBe('on')
    expect(find(CacheView(props()), 'cache-setting-state-live')!.props['data-tone']).toBe('warn')
    expect(text).toContain('A pass-through data source is never cached.')
    expect(find(CacheView(props()), 'cache-setting-state-hr')!.props['data-tone']).toBe('off')
    expect(text).toContain('data-source/erp')
  })

  it('says the cache is off when no definition turns it on', () => {
    expect(html({ data: overview({ enabled: false }) })).toContain('The cache is off')
    expect(html()).not.toContain('The cache is off')
  })

  it('offers a clear to an administrator only, for an enabled definition and for everything', () => {
    expect(find(CacheView(props()), 'cache-clear-erp')).toBeDefined()
    expect(find(CacheView(props()), 'cache-clear-hr')).toBeUndefined()
    expect(find(CacheView(props()), 'cache-clear-all')).toBeDefined()
    expect(find(CacheView(props({ isAdmin: false })), 'cache-clear-erp')).toBeUndefined()
    expect(find(CacheView(props({ isAdmin: false })), 'cache-clear-all')).toBeUndefined()
  })

  it('asks for a second click and reports what a clear removed', () => {
    const onClear = vi.fn()
    expect(html({ confirming: 'data-source/erp' })).toContain('Click again to clear');
    (find(CacheView(props({ onClear })), 'cache-clear-erp')!.props.onClick as () => void)()
    expect(onClear).toHaveBeenCalledWith('data-source/erp')
    expect(html({ cleared: '1 entry cleared.' })).toContain('1 entry cleared.')
  })

  it('says when nothing was asked of the cache and when the group shares changes', () => {
    expect(html({ data: overview({ datasets: [] }) })).toContain('Nothing has been asked of the cache yet.')
    expect(html({ data: overview({ events: { ...overview().events, shared: true } }) })).toContain('one of a group')
  })
})
