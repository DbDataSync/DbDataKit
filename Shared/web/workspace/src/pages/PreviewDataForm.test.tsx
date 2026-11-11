import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { PublishTarget } from '../api'
import { publishedLine } from '../model'
import { PreviewDataForm, previewDataButton } from './PreviewDataForm'

const targets: PublishTarget[] = [
  { name: 'serving', gateway: 'https://gateway:8816', product: 'gateway', accepts: true, can_publish: true },
  { name: 'jobs', gateway: 'https://runner:8495', product: 'runner', accepts: false, can_publish: true },
]
const draw = (props: Partial<Parameters<typeof PreviewDataForm>[0]> = {}) =>
  renderToStaticMarkup(<PreviewDataForm canWrite status="succeeded" targets={targets} busy={false} onSend={() => {}} {...props} />)

describe('Preview with data', () => {
  it('is held until the build succeeded, the person may write, and a key and an instance are named, each said in words', () => {
    const ok = { canWrite: true, status: 'succeeded', target: 'serving', instance: 'acme', busy: false }
    expect(previewDataButton(ok)).toEqual({ enabled: true, why: null })
    expect(previewDataButton({ ...ok, canWrite: false }).why).toContain('author role')
    expect(previewDataButton({ ...ok, status: 'failed' }).why).toContain('succeeded')
    expect(previewDataButton({ ...ok, target: ' ' }).why).toContain('publisher key')
    expect(previewDataButton({ ...ok, instance: '' }).why).toContain('instance')
    expect(previewDataButton({ ...ok, busy: true }).enabled).toBe(false)
  })

  it('draws the key, the instance and the button, offers the Gateways that take apps, and says what the preview does', () => {
    const html = draw({ initialTarget: 'serving', initialInstance: 'acme' })
    for (const id of ['wsp-preview-data-target', 'wsp-preview-data-instance', 'wsp-preview-data']) expect(html).toContain(`data-testid="${id}"`)
    expect(html).toContain('<option value="serving">')
    expect(html).not.toContain('<option value="jobs">')
    expect(html).toContain('changes nothing')
    expect(html).not.toContain('disabled=""')
    expect(draw()).toContain('disabled=""')
    expect(draw({ canWrite: false })).toContain('Sending a preview needs the author role')
  })

  it('a record of a preview names the instance and the preview channel, not a publication', () => {
    expect(publishedLine({ app: 'orders', instance: 'acme', gateway: 'https://gateway:8816', version: 3 })).toBe('Sent orders to https://gateway:8816 as the preview of acme (version 3 of the preview channel).')
    expect(publishedLine({ app: 'orders', gateway: 'https://gateway:8816', version: 3 })).toBe('Published orders to https://gateway:8816 as version 3.')
  })
})
