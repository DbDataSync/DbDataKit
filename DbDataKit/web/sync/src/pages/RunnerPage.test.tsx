import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { PublishedReplicationsTable, RunnerPackageCard, SlotBindings } from './RunnerPage'
import { bindingsToSend, packageState, scheduleText, slotChoices } from './runnerPublish'
import type { ReplicationSetPublication, RunnerPackageView } from '../api/types'

const view = (patch: Partial<RunnerPackageView> = {}): RunnerPackageView => ({
  linked: true, server: 'runner-1', channel: 'prod', package: 'sales-set', published: true, latest: 4, active: 3, previous: 2, staged: null,
  staging: null, slots: [], instances: [], events: [], ...patch,
})

const check: ReplicationSetPublication = {
  package: 'sales-set', channel: null, mode: 'check', version: null, changed: null, uploaded: null, skipped: null, files: 12, bound: null,
  replications: [
    { replication: 'orders', task: 'orders', mode: 'Periodic', cron: '*/15 * * * *', frequencySeconds: null, slots: ['crm_src', 'dwh'], mappings: 2, slotConnections: { crm_src: 'crm-src', dwh: 'dwh' } },
    { replication: 'events', task: 'events', mode: 'Continuous', cron: null, frequencySeconds: 30, slots: ['crm_src', 'dwh'], mappings: 1, slotConnections: { crm_src: 'crm-src', dwh: 'dwh' } },
  ],
}

describe('where the package stands', () => {
  it('says there is no runner, nothing published, or what is served', () => {
    expect(packageState(undefined).key).toBe('unlinked')
    expect(packageState(view({ linked: false })).key).toBe('unlinked')
    expect(packageState(view({ published: false })).key).toBe('unpublished')
    expect(packageState(view())).toEqual({ key: 'serving', text: 'Version 3 is served in prod' })
  })

  it('says how far the runners are with a staged version, and when one failed', () => {
    const staging = { version: 4, allReady: false, summary: '1 of 2 instances ready', failed: [], needsConfirmation: false, losses: [] }
    expect(packageState(view({ staged: 4, staging }))).toEqual({ key: 'staged', text: 'Version 4 is staged: 1 of 2 instances ready' })
    expect(packageState(view({ staged: 4, staging: { ...staging, allReady: true } })).text).toBe('Version 4 is staged and ready on every runner')
    expect(packageState(view({ staged: 4, staging: { ...staging, failed: ['b: slot dwh is not bound in prod'] } })).key).toBe('failing')
  })
})

describe('the package card', () => {
  it('shows the served and staged versions and the runners', () => {
    const html = renderToStaticMarkup(<RunnerPackageCard view={view({
      staged: 4,
      staging: { version: 4, allReady: false, summary: '1 of 2 instances ready', failed: ['b: slot dwh is not bound in prod'], needsConfirmation: true, losses: ['orders: watermark'] },
      instances: [{ instance: 'a', version: 4, state: 'ready', error: null, note: null, gone: false, at: null }, { instance: 'b', version: 4, state: 'failed', error: 'slot dwh is not bound in prod', note: null, gone: false, at: null }],
    })} />)
    expect(html).toContain('sales-set on runner-1, channel prod')
    expect(html).toContain('data-testid="runner-package-active">3 (the one before: 2)')
    expect(html).toContain('4: 1 of 2 instances ready')
    expect(html).toContain('data-testid="runner-staging-failed"')
    expect(html).toContain('slot dwh is not bound in prod')      // the runner's own words
    expect(html).toContain('orders: watermark')
    expect(html).toContain('data-testid="runner-instance-b"')
  })

  it('says to publish when the runner has no such package, and to set the runner when there is none', () => {
    expect(renderToStaticMarkup(<RunnerPackageCard view={view({ published: false })} />)).toContain('publish the replication set below')
    expect(renderToStaticMarkup(<RunnerPackageCard view={view({ linked: false, published: false })} />)).toContain('data-testid="runner-package-unlinked"')
    expect(renderToStaticMarkup(<RunnerPackageCard view={undefined} />)).toContain('data-testid="runner-package-unlinked"')
  })
})

describe('what a publish would hold', () => {
  it('has a row for each replication with its task, its schedule on the runner and its slots', () => {
    const html = renderToStaticMarkup(<PublishedReplicationsTable check={check} />)
    expect(html).toContain('data-testid="runner-check-row-orders"')
    expect(html).toContain('periodic, */15 * * * *')
    expect(html).toContain('continuous, every 30 s')
    expect(html).toContain('crm_src, dwh')
    expect(scheduleText(check.replications[1]!)).toBe('continuous, every 30 s')
  })
})

describe('the slots', () => {
  it('are the ones the publish declares, filled with the runner\'s binding or the Sync connection\'s own name', () => {
    const choices = slotChoices(check, view({ slots: [{ slot: 'dwh', kind: 'postgres', tasks: ['orders'], versions: [3], boundTo: 'dwh-prod', revision: 2 }] }), {})
    expect(choices.map((c) => [c.slot, c.value, c.boundTo])).toEqual([['crm_src', 'crm-src', null], ['dwh', 'dwh-prod', 'dwh-prod']])
  })

  it('send what is not bound to exactly that already, trimmed, and nothing for an empty box', () => {
    const edited = slotChoices(check, view({ slots: [{ slot: 'dwh', kind: 'postgres', tasks: [], versions: [], boundTo: 'dwh-prod', revision: 2 }] }), { crm_src: ' crm-prod ', dwh: 'dwh-prod' })
    expect(bindingsToSend(edited)).toEqual({ crm_src: 'crm-prod' })
    expect(bindingsToSend(slotChoices(check, undefined, { crm_src: '', dwh: '' }))).toEqual({})
  })

  it('say that a slot nobody bound is not activated, and are disabled for a viewer', () => {
    const choices = slotChoices(check, view(), {})
    const html = renderToStaticMarkup(<SlotBindings choices={choices} edits={{}} onEdit={() => {}} disabled />)
    expect(html).toContain('data-testid="runner-slot-unbound-crm_src"')
    expect(html).toContain('the runner will not activate a version that declares it')
    expect(html).toContain('disabled=""')
  })

  it('are listed from the runner alone when no check has run', () => {
    expect(slotChoices(undefined, view({ slots: [{ slot: 'a', kind: 'mssql', tasks: [], versions: [], boundTo: null, revision: null }] }), {}).map((c) => c.slot)).toEqual(['a'])
  })
})
