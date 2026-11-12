import { describe, expect, it } from 'vitest'
import { alertFormFrom, alertSummary, buildAlertRule, cooldownWords, bindingLine, bindingProblem, buildRequest, channelProblem, describeSchedule, emptyForm, formFromSubscription, outboxLine, sendNowBlockedReason, statusWord, subscriptionProblem, type ChannelBinding, type Subscription, type SubscriptionForm } from './delivery'

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

  it('asks for the inline picture only of a PDF, and gives it back when editing', () => {
    expect(buildRequest(form({ inlinePreview: true }))).toMatchObject({ body: { inline_preview: true } })
    expect(buildRequest(form({ inlinePreview: false }))).not.toHaveProperty('body.inline_preview')
    expect(buildRequest(form({ format: 'xlsx', inlinePreview: true }))).not.toHaveProperty('body.inline_preview')   // a workbook has no pages
    expect(formFromSubscription(subscription({ inlinePreview: true })).inlinePreview).toBe(true)
    expect(formFromSubscription(subscription()).inlinePreview).toBe(false)
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

const binding = (over: Partial<ChannelBinding> = {}): ChannelBinding => ({ release: 'prod', connection: 'slack-ops', own: true, kind: 'slack', target: 'https://hooks.slack.com/services/{token}', ready: true, missing: [], problem: null, ...over })
describe('what a channel\'s bindings say in words (the mapping itself is the Bindings panel: bindings.test.ts)', () => {
  it('says in words where a release channel goes and why a binding cannot be used', () => {
    expect(bindingLine(binding({ release: 'test', connection: 'slack-sandbox' }))).toBe('test uses slack-sandbox (slack, https://hooks.slack.com/services/{token})')
    expect(bindingLine(binding({ release: '*', connection: 'slack-sandbox', own: true }))).toContain('every other release channel uses slack-sandbox')
    expect(bindingProblem(binding())).toBeNull()
    expect(bindingProblem(binding({ ready: false, problem: 'There is no connection named \'x\'.' }))).toBe('There is no connection named \'x\'.')
    expect(bindingProblem(binding({ ready: false, missing: ['endpoint_secrets.token: env:T cannot be read'] }))).toBe('endpoint_secrets.token: env:T cannot be read')
  })
})

describe('the alert rules', () => {
  const rule = { kind: 'failed-run', title: 'Failed runs', enabled: true, channel: 'alerts', recipients: ['ops@example.com'], cooldownMinutes: 60, releaseChannel: null, version: 2 }

  it('builds the body the API reads from what was typed, recipients in any separator', () => {
    const form = { ...alertFormFrom(rule), recipients: 'ops@example.com; ana@example.com\nbo@example.com', cooldownMinutes: '30' }
    expect(buildAlertRule(form, 2)).toEqual({ body: { enabled: true, channel: 'alerts', recipients: ['ops@example.com', 'ana@example.com', 'bo@example.com'], cooldownMinutes: 30, releaseChannel: null, version: 2 } })
  })

  it('says what is wrong in words before asking the server', () => {
    expect(buildAlertRule({ ...alertFormFrom(rule), recipients: 'nobody' }, 2)).toEqual({ error: "'nobody' is not an email address." })
    expect(buildAlertRule({ ...alertFormFrom(rule), channel: '' }, 2)).toEqual({ error: 'Recipients need a channel; with no channel the alert is shown in the notification feed only.' })
    expect(buildAlertRule({ ...alertFormFrom(rule), cooldownMinutes: '0' }, 2)).toEqual({ error: 'The cool-down is a whole number of minutes, from 1 to 10080 (7 days).' })
    expect(buildAlertRule({ ...alertFormFrom(rule), cooldownMinutes: 'soon' }, 2)).toMatchObject({ error: expect.stringContaining('whole number') })
  })

  it('summarises a rule in a sentence and the cool-down in the unit a person reads', () => {
    expect(alertSummary({ ...rule, enabled: false })).toBe('Off.')
    expect(alertSummary(rule)).toBe('On: sent through alerts to ops@example.com; a source that keeps failing is told again after an hour.')
    expect(alertSummary({ ...rule, channel: null, recipients: [], cooldownMinutes: 1440 })).toBe('On: shown in the notification feed only; a source that keeps failing is told again after a day.')
    expect([cooldownWords(45), cooldownWords(120), cooldownWords(2880)]).toEqual(['45 minutes', '2 hours', '2 days'])
  })
})

describe('a subscription to a custom page (plan 32 DBR34)', () => {
  it('is named instance.page, is a PDF and has no layout or widget', () => {
    const built = buildRequest(form({ kind: 'page', view: 'sales-portal.regional-detail', layout: 'print', widget: 'w' }))
    expect(built).toEqual({ body: expect.objectContaining({ kind: 'page', view: 'sales-portal.regional-detail', format: 'pdf' }) })
    expect('body' in built && 'layout' in built.body).toBe(false)
    expect(buildRequest(form({ kind: 'page', view: 'regional-detail' }))).toEqual({ error: expect.stringContaining('<app instance>.<page name>') })
    expect(buildRequest(form({ kind: 'page', view: 'a.b', format: 'xlsx' }))).toEqual({ error: expect.stringContaining('PDF') })
    expect(buildRequest(form({ kind: 'page', view: '' }))).toEqual({ error: expect.stringContaining('Name the page') })
  })
})
