import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { PublishTarget } from '../api'
import { adoptionRequest, adoptionSentence, makeLabel, parseBindings, publishButton, publishedLine } from '../model'
import { PublishForm } from './PublishForm'

const targets: PublishTarget[] = [
  { name: 'jobs', gateway: 'https://runner:8495', product: 'runner', accepts: true, can_publish: true },
  { name: 'edge', gateway: 'https://gateway:8816', product: 'gateway', accepts: false, can_publish: true },
  { name: 'serving', gateway: 'https://gateway2:8816', product: 'gateway', accepts: true, can_publish: true },
]
const draw = (props: Partial<Parameters<typeof PublishForm>[0]> = {}) =>
  renderToStaticMarkup(<PublishForm canWrite status="succeeded" targets={targets} busy={false} onPublish={() => {}} {...props} />)

describe('the publish form by the product of the target', () => {
  it('a Runner asks for the channel, the slots to bind and whether to serve at once, and a Gateway of apps asks for none of it', () => {
    const runner = draw({ initialTarget: 'jobs' })
    expect(runner).toContain('data-product="runner"')
    for (const id of ['wsp-publish-channel', 'wsp-publish-bind', 'wsp-publish-serve']) expect(runner).toContain(`data-testid="${id}"`)
    expect(runner).toContain('disabled=""')                       // no channel yet
    expect(runner).toContain('Name the channel of the Runner the package goes to.')
    const gateway = draw({ initialTarget: 'serving' })
    expect(gateway).toContain('data-product="gateway"')
    for (const id of ['wsp-publish-channel', 'wsp-publish-bind', 'wsp-publish-serve', 'wsp-publish-datasource']) expect(gateway).not.toContain(`data-testid="${id}"`)
  })

  it('a Gateway that takes models asks for the data source, blank meaning the storage connection of the model', () => {
    const html = draw({ initialTarget: 'serving', models: true })
    expect(html).toContain('data-testid="wsp-publish-datasource"')
    expect(html).toContain('blank: the model&#x27;s storage connection')
  })

  it('offers only the targets the kind of the project is published to', () => {
    const html = draw()
    expect(html).toContain('<option value="jobs">')
    expect(html).toContain('<option value="serving">')
    expect(html).not.toContain('<option value="edge">')
  })

  it('names why a target of the wrong product cannot be used when it is typed anyway', () => {
    expect(draw({ initialTarget: 'edge' })).toContain('This kind of project is not published to a Gateway')
  })
})

describe('the words of a publish', () => {
  it('the button is held until a Runner has its channel, and a wrong product is said in words', () => {
    const ok = { canWrite: true, status: 'succeeded', target: 'jobs', targets, busy: false }
    expect(publishButton(ok).why).toContain('channel')
    expect(publishButton({ ...ok, channel: 'prod' })).toEqual({ enabled: true, why: null })
    expect(publishButton({ ...ok, target: 'edge' }).why).toContain('not published to a Gateway')
    expect(publishButton({ ...ok, targets: [{ ...targets[0]!, can_publish: false }], channel: 'prod' }).why).toBe('The key jobs is not paired with a Runner that lets it publish.')
  })

  it('bindings are slot=connection pairs, separated by commas or lines, and a pair with a missing side is named', () => {
    expect(parseBindings('')).toEqual({ bind: {}, problem: null })
    expect(parseBindings('source_db=crm, target_db = dwh\nx=y')).toEqual({ bind: { source_db: 'crm', target_db: 'dwh', x: 'y' }, problem: null })
    expect(parseBindings('source_db=crm, nope').problem).toBe('"nope" is not slot=connection.')
    expect(parseBindings('a=').problem).toContain('"a="')
  })

  it('says what was published, whatever it was', () => {
    expect(publishedLine({ app: 'orders', gateway: 'https://g', version: 3 })).toBe('Published orders to https://g as version 3.')
    expect(publishedLine({ package: 'orders-set', channel: 'prod', mode: 'stage', version: 2, changed: true, runner: 'https://r' })).toBe('Staged orders-set on prod of https://r as version 2.')
    expect(publishedLine({ package: 'orders-set', channel: 'prod', mode: 'stage', version: 1, changed: false })).toContain('nothing changed')
    expect(publishedLine({ package: 'p', channel: 'c', mode: 'serve', version: 1 })).toMatch(/^Published p on c/)
    expect(publishedLine({ gateway: 'https://g', models: [{ model: 'territories', data_source: 'mdm', version: 2, unchanged: false }] })).toBe('Published territories on the data source mdm as version 2 to https://g.')
  })

  it('the button that makes what a kind publishes says what it makes', () => {
    expect(makeLabel('app')).toBe('Build')
    expect(makeLabel('sync')).toBe('Check and make the package')
    expect(makeLabel('build')).toBe('Check and make the package and the models')
    expect(makeLabel('store')).toBe('Check and make the models')
  })
})

describe('adopting a data source made by hand (plan 33 SC21f)', () => {
  const refusal = { error: 'The Gateway refused ...', steps: [], adopt: { data_source: 'mdm', made_by: 'hand', publish_adopt: true } }

  it('reads the data source and who made it from the Gateway refusal, and nothing from any other refusal', () => {
    expect(adoptionRequest(refusal)).toEqual({ data_source: 'mdm', made_by: 'hand' })
    expect(adoptionRequest({ error: 'no' })).toBeNull()
    expect(adoptionRequest(null)).toBeNull()
    expect(adoptionRequest('text')).toBeNull()
    expect(adoptionRequest({ adopt: { made_by: 'hand' } })).toBeNull()
  })

  it('the confirmation names the data source, says it was made by hand and that the Gateway records who and when', () => {
    const text = adoptionSentence({ data_source: 'mdm', made_by: 'hand' })
    expect(text).toContain('The data source mdm on the Gateway was made by hand or by an administrator')
    expect(text).toContain('records that you adopted it (who, when and which data source)')
  })

  it('shows the confirmation only when the Gateway asked for one, with both buttons', () => {
    expect(draw({ initialTarget: 'serving', models: true })).not.toContain('wsp-publish-adopt')
    const html = draw({ initialTarget: 'serving', models: true, adoption: { data_source: 'mdm', made_by: 'hand' } })
    expect(html).toContain('data-testid="wsp-publish-adopt"')
    expect(html).toContain('data-data-source="mdm"')
    expect(html).toContain('Confirm and publish onto mdm')
    expect(html).toContain('data-testid="wsp-publish-adopt-cancel"')
    expect(draw({ initialTarget: 'serving', models: true, adoption: { data_source: 'mdm', made_by: 'hand' }, busy: true })).not.toContain('wsp-publish-adopt"')   // not while it publishes
  })

  it('a Build project\'s models say which data sources they are on and which were adopted', () => {
    expect(publishedLine({ gateway: 'https://g', models: [{ model: 'sales', data_sources: { default: 'warehouse', b: 'warehouse' }, version: 1, adopted: ['warehouse'] }] }))
      .toBe('Published sales on the data source warehouse as version 1 (adopted warehouse, made by hand) to https://g.')
    expect(publishedLine({ gateway: 'https://g', models: [{ model: 't', data_source: 'mdm', version: 2, adopted: 'mdm' }] })).toContain('(adopted mdm, made by hand)')
  })
})
