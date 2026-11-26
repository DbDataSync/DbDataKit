import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { DeployTarget } from '../deploys'
import { deployLine, type DeployRecord } from '../deploys'
import { publishedLine } from '../model'
import { PreviewDataForm, previewDataButton } from './PreviewDataForm'

const targets: DeployTarget[] = [
  { id: 'g-1', name: 'serving', product: 'gateway', reachable: true, accepts: true },
  { id: 'g-2', name: 'edge', product: 'gateway', reachable: false, accepts: true },
  { id: 'r-1', name: 'jobs', product: 'runner', reachable: true, accepts: false },
]
const draw = (props: Partial<Parameters<typeof PreviewDataForm>[0]> = {}) =>
  renderToStaticMarkup(<PreviewDataForm canWrite status="succeeded" targets={targets} busy={false} onSend={() => {}} {...props} />)

describe('Preview with data', () => {
  it('is held until the build succeeded, the person may write, and a Gateway that answers and an instance are chosen, each said in words', () => {
    const ok = { canWrite: true, status: 'succeeded', target: 'g-1', targets, instance: 'acme', busy: false }
    expect(previewDataButton(ok)).toEqual({ enabled: true, why: null })
    expect(previewDataButton({ ...ok, canWrite: false }).why).toContain('author role')
    expect(previewDataButton({ ...ok, status: 'failed' }).why).toContain('succeeded')
    expect(previewDataButton({ ...ok, target: '' }).why).toBe('Choose the Gateway the preview goes to.')
    expect(previewDataButton({ ...ok, target: '', targets: [targets[2]!] }).why).toContain('manages no Gateway')
    expect(previewDataButton({ ...ok, target: 'r-1' }).why).toBe('Choose a Gateway the console manages.')
    expect(previewDataButton({ ...ok, target: 'g-2' }).why).toBe('edge is not answering the console.')
    expect(previewDataButton({ ...ok, instance: '' }).why).toContain('instance')
    expect(previewDataButton({ ...ok, busy: true }).enabled).toBe(false)
  })

  it('offers the Gateways the console manages, never a Runner, and says what the preview does', () => {
    const html = draw({ initialTarget: 'g-1', initialInstance: 'acme' })
    for (const id of ['wsp-preview-data-target', 'wsp-preview-data-instance', 'wsp-preview-data']) expect(html).toContain(`data-testid="${id}"`)
    expect(html).toContain('<option value="g-1" selected="">serving</option>')
    expect(html).toContain('<option value="g-2">edge (not answering)</option>')
    expect(html).not.toContain('value="r-1"')
    expect(html).toContain('changes nothing')
    expect(html).not.toContain('disabled=""')
    expect(draw()).toContain('disabled=""')
    expect(draw({ canWrite: false })).toContain('Sending a preview needs the author role')
  })

  it('a record of a preview names the instance and the preview channel, not a publication', () => {
    expect(publishedLine({ app: 'orders', instance: 'acme', gateway: 'https://gateway:8816', version: 3 })).toBe('Sent orders to https://gateway:8816 as the preview of acme (version 3 of the preview channel).')
    expect(publishedLine({ app: 'orders', gateway: 'https://gateway:8816', version: 3 })).toBe('Published orders to https://gateway:8816 as version 3.')
    const record = { id: 'd1', workspace_server: 'ws-1', project: 'orders', workspace: 'd1', build: 'b1', item: 'app', kind: 'app', target: 'g-1', target_name: 'serving', product: 'gateway', route: 'app', name: 'orders',
      channel: 'preview', version: 3, mode: 'preview', signature: null, deployed_by: 'ana', deployed_at: '', outcome: 'ok', error: null, preview: { instance: 'acme', address: null, path: '/apps/acme~preview/' } } satisfies DeployRecord
    expect(deployLine(record)).toBe('Sent orders to serving as the preview of acme (version 3 of the preview channel).')
    expect(deployLine({ ...record, mode: null, channel: null, preview: null })).toBe('Published orders to serving as version 3.')
  })
})
