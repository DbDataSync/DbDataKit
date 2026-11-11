import { describe, expect, it } from 'vitest'
import { buildRequest, channelProblem, describeSchedule, emptyForm, formFromSubscription, outboxLine, sendNowBlockedReason, statusWord, subscriptionProblem, type Subscription, type SubscriptionForm } from './delivery'

const form = (over: Partial<SubscriptionForm> = {}): SubscriptionForm => ({ ...emptyForm('mail'), name: 'Weekly sales', view: 'sales', ...over })

const subscription = (over: Partial<Subscription> = {}): Subscription => ({
  id: 'id-1', name: 'Weekly sales', kind: 'dashboard', view: 'sales', parameters: { region: ['east'] }, layout: 'print', format: 'pdf', widget: null, channel: 'mail', recipient: null, schedule: { cron: '0 7 * * 1', timeZone: 'Europe/Berlin' },
  person: 'u1', personName: 'Ana', owner: 'u1', status: 'active', statusDetail: null, nextRunAt: 't', lastRunAt: null, lastOutcome: null, lastDetail: null, version: 1, channelProblem: null, ...over,
})

describe('the subscribe form', () => {
  it('builds the strict request the API reads, with a PDF taking a layout and a CSV a widget', () => {
    const pdf = buildRequest(form({ layout: ' print ', parameters: '{"region": ["east"], "year": 2026}', recipient: 'ana@example.com' }))
    expect(pdf).toEqual({ body: { name: 'Weekly sales', kind: 'dashboard', view: 'sales', format: 'pdf', channel: 'mail', layout: 'print', recipient: 'ana@example.com', parameters: { region: ['east'], year: 2026 }, schedule: { cron: '0 7 * * 1', time_zone: 'UTC' } } })
    const csv = buildRequest(form({ format: 'csv', widget: 'by_region', layout: 'print' }))
    expect(csv).toEqual({ body: { name: 'Weekly sales', kind: 'dashboard', view: 'sales', format: 'csv', channel: 'mail', widget: 'by_region', schedule: { cron: '0 7 * * 1', time_zone: 'UTC' } } })   // a layout is a PDF's: it is left out
  })

  it('says what is missing in words before asking the server', () => {
    expect(buildRequest(form({ name: ' ' }))).toEqual({ error: 'Give the subscription a name.' })
    expect(buildRequest(form({ view: '' }))).toEqual({ error: 'Name the dashboard to subscribe to.' })
    expect(buildRequest(form({ kind: 'report', view: '' }))).toEqual({ error: 'Name the report to subscribe to.' })
    expect(buildRequest(form({ channel: '' }))).toEqual({ error: 'Choose a delivery channel.' })
    expect(buildRequest(form({ format: 'csv' }))).toEqual({ error: 'A CSV is one widget’s rows: name the widget.' })
    expect(buildRequest(form({ parameters: '{nope' }))).toMatchObject({ error: expect.stringContaining('not valid JSON') })
    expect(buildRequest(form({ parameters: '[1]' }))).toMatchObject({ error: expect.stringContaining('an object') })
  })

  it('opens a subscription for editing as the form that made it', () => {
    const f = formFromSubscription(subscription())
    expect(f).toMatchObject({ name: 'Weekly sales', view: 'sales', layout: 'print', channel: 'mail', cron: '0 7 * * 1', timeZone: 'Europe/Berlin', parameters: '{"region":["east"]}' })
    expect(buildRequest(f)).toMatchObject({ body: { schedule: { cron: '0 7 * * 1', time_zone: 'Europe/Berlin' }, parameters: { region: ['east'] } } })
  })
})

describe('what a person reads', () => {
  it('says the outbox in a line and the states in words', () => {
    expect(outboxLine({ pending: 1, sending: 0, sent: 3, dead: 2 })).toBe('1 waiting, 3 sent, 2 failed')
    expect(outboxLine({})).toBe('nothing yet')
    expect(statusWord('dead')).toBe('Failed')
    expect(channelProblem({ ready: true, missing: [] })).toBeNull()
    expect(channelProblem({ ready: false, missing: ['url: env:X cannot be read'] })).toBe('url: env:X cannot be read')
    expect(channelProblem({ ready: false, missing: [] })).toBe('Not ready.')
  })

  it('names the schedule the way a person would', () => {
    expect(describeSchedule('0 7 * * 1', 'Europe/Berlin')).toBe('every Monday at 07:00 (Europe/Berlin)')
    expect(describeSchedule('30 8 * * *', 'UTC')).toBe('every day at 08:30 (UTC)')
    expect(describeSchedule('0 7 * * 1-5', 'UTC')).toBe('on weekdays at 07:00 (UTC)')
    expect(describeSchedule('0 7 * * 1,3', 'UTC')).toBe('on Mon, Wed at 07:00 (UTC)')
    expect(describeSchedule('0 7 1 * *', 'UTC')).toBe('0 7 1 * * (UTC)')
  })

  it('shows the most important problem of a subscription first', () => {
    expect(subscriptionProblem(subscription({ status: 'stopped', statusDetail: 'Stopped: Bo is no longer in the group.', channelProblem: 'gone' }))).toBe('Stopped: Bo is no longer in the group.')
    expect(subscriptionProblem(subscription({ channelProblem: 'The channel \'mail\' is not configured.' }))).toBe('The channel \'mail\' is not configured.')
    expect(subscriptionProblem(subscription({ statusDetail: 'the host has no browser' }))).toBe('the host has no browser')
    expect(subscriptionProblem(subscription({ lastOutcome: 'failed', lastDetail: 'the export did not finish' }))).toBe('the export did not finish')
    expect(subscriptionProblem(subscription())).toBeNull()
  })

  it('does not offer send-now where the console cannot draw the document or the channel is gone', () => {
    expect(sendNowBlockedReason(false, 'This console does not draw documents.', subscription())).toBe('This console does not draw documents.')
    expect(sendNowBlockedReason(true, null, subscription({ channelProblem: 'The channel is gone.' }))).toBe('The channel is gone.')
    expect(sendNowBlockedReason(true, null, subscription())).toBeNull()
  })
})
