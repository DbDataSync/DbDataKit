import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { customDriversOf, registryDescriptors, syncEnginesFrom } from './syncEngines'
import { EngineNote, EngineOptions, SyncFileEditor, SyncSetPanel } from './pages/SyncSetPanel'
import * as engines from './syncEngines'
import type { LiveCheck } from './useLiveCheck'

const ids = (list: { id: string }[]) => list.map((e) => e.id)
const offered = (list: { id: string; source: { offered: boolean }; target: { offered: boolean } }[], role: 'source' | 'target') => ids(list.filter((e) => e[role].offered))

describe('the engine lists of a sync project', () => {
  const lists = syncEnginesFrom(registryDescriptors(), [])

  it('are the registry\'s Sync roles: the same engines EngineRegistryTests holds the registry to', () => {
    expect(registryDescriptors().length).toBeGreaterThanOrEqual(9)
    expect(offered(lists.sources, 'source')).toEqual(['duckdb', 'flightsql', 'mysql', 'oracle', 'postgres', 'sqlserver'])
    expect(offered(lists.targets, 'target')).toEqual(['mysql', 'oracle', 'postgres', 'sqlserver'])
    // an engine with no Sync role at all (ANSI, ODBC, Fabric) is not listed
    expect(ids(lists.all)).not.toContain('ansi')
    expect(ids(lists.all)).not.toContain('fabric')
  })

  it('carry the role\'s status and note, and find an engine by an alias', () => {
    const pg = lists.find('postgresql')!
    expect(pg.id).toBe('postgres')
    expect(pg.source.status).toBe('supported')
    expect(pg.target.note).toContain('COPY')
    expect(lists.find('nothing')).toBeUndefined()
  })

  it('show a planned role as planned and never offer it', () => {
    const planned = 'schema: engine/1\nid: newdb\ndisplay_name: New DB\nroles:\n  sync_source: { status: supported }\n  sync_target: { status: planned, note: "a later slice" }\n'
    const l = syncEnginesFrom([...registryDescriptors(), planned], [])
    const e = l.find('newdb')!
    expect(e.target).toEqual({ status: 'planned', note: 'a later slice', offered: false })
    expect(ids(l.targets)).toContain('newdb')
    expect(offered(l.targets, 'target')).not.toContain('newdb')
    expect(offered(l.sources, 'source')).toContain('newdb')
    const html = renderToStaticMarkup(<select><EngineOptions engines={l} /></select>)
    expect(html).toContain('newdb: source (target planned)')
    const note = renderToStaticMarkup(<EngineNote text={'schema: connection/1\nname: x\nengine: newdb\n'} engines={l} />)
    expect(note).toContain('data-target="planned"')
    expect(note).toContain('planned: a later slice (not offered)')
  })

  it('list a driver the draft carries as custom', () => {
    const paths = ['config/connections/a.yaml', 'drivers/mysql.generic/driver.yaml', 'drivers/legacy/driver.csx', 'drivers/legacy/lib/x.dll']
    expect(customDriversOf(paths)).toEqual(['legacy', 'mysql.generic'])
    const l = syncEnginesFrom(registryDescriptors(), paths)
    expect(l.find('mysql.generic')).toMatchObject({ custom: true, source: { offered: true }, target: { offered: true } })
    const html = renderToStaticMarkup(<select><EngineOptions engines={l} /></select>)
    expect(html).toContain('mysql.generic (custom)')
  })
})

describe('the one hook both areas read', () => {
  const noLive: LiveCheck = { diagnostics: [], checkedText: null, failed: null, running: false } as unknown as LiveCheck

  it('gives the Files area\'s new-connection form its engines', () => {
    const html = renderToStaticMarkup(<SyncSetPanel paths={['drivers/mine/driver.yaml']} canWrite busy={false} onOpen={() => {}} onCreate={() => {}} />)
    expect(html).toContain('mine (custom)')
    expect(html).toContain('value="sqlserver"')
    expect(html).not.toContain('value="ansi"')
  })

  it('gives the connection form its note from the registry', () => {
    const html = renderToStaticMarkup(<SyncFileEditor kind="sync-connection" path="config/connections/dw.yaml" text={'schema: connection/1\nname: dw\nengine: flightsql\n'} onText={() => {}} readOnly live={noLive} paths={[]} />)
    expect(html).toContain('data-testid="wsp-engine-note"')
    expect(html).toContain('data-source="supported"')
    expect(html).toContain('data-target="no"')
  })

  it('is exported for the Designer from the package', async () => {
    const index = await import('./index')
    expect(index.useSyncEngines).toBe(engines.useSyncEngines)
  })
})
