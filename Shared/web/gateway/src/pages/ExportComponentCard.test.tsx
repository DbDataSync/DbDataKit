import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import type { ExportComponentState } from '../types'
import { ExportComponentView, type ExportComponentViewProps } from './ExportComponentCard'

const state = (over: Partial<ExportComponentState> = {}, install: Partial<ExportComponentState['install']> = {}): ExportComponentState => ({
  platform: 'linux-x64', supported: true, installed: false, whole: false, directory: '/var/lib/dbdatakit/gateway/export-component', release: null, chromium: null, fonts: null, mode: null, configured: false, pins_verified: null, pins_note: null, problems: [],
  summary: 'PDF export is not installed.', install: { state: 'idle', step: '', bytes: 0, total: 0, message: null, restart_needed: false, ...install }, ...over,
})

const props = (over: Partial<ExportComponentViewProps> = {}): ExportComponentViewProps => ({ loading: false, loadError: null, state: state(), busy: false, refusal: null, allowed: true, onInstall: () => {}, ...over })

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  return find(element.props.children as ReactNode, testId)
}

const tree = (over?: Partial<ExportComponentViewProps>) => ExportComponentView(props(over))
const html = (over?: Partial<ExportComponentViewProps>) => renderToStaticMarkup(tree(over))

describe('the PDF export component card on the server settings screen', () => {
  it('says the component is not installed and offers Install, which asks for a first install', () => {
    const onInstall = vi.fn()
    expect(html()).toContain('PDF export is not installed.')
    const button = find(tree({ onInstall }), 'export-install')!
    expect(button.props.disabled).toBe(false)
    ;(button.props.onClick as () => void)()
    expect(onInstall).toHaveBeenCalledWith(false)
    expect(find(tree(), 'export-component')!.props['data-state']).toBe('missing')
  })

  it('offers no install to a caller who may not, and none while the call is in flight', () => {
    expect(find(tree({ allowed: false }), 'export-install')!.props.disabled).toBe(true)
    expect(find(tree({ busy: true }), 'export-install')!.props.disabled).toBe(true)
  })

  it('shows the step and the bytes while it runs, and no button', () => {
    const running = state({}, { state: 'running', step: 'downloading dbdatagateway-export-v1-linux-x64.zip', bytes: 100 * 1024 * 1024, total: 400 * 1024 * 1024 })
    const text = html({ state: running })
    expect(text).toContain('downloading dbdatagateway-export-v1-linux-x64.zip')
    expect(text).toContain('100 MB of 400 MB')
    expect(find(tree({ state: running }), 'export-install')).toBeUndefined()
    expect(find(tree({ state: running }), 'export-component')!.props['data-state']).toBe('running')
  })

  it('says why an install was refused, in the Gateway\'s words', () => {
    const failed = state({}, { state: 'failed', message: 'dbdatagateway-export-v1-linux-x64.zip does not match SHA256SUMS: its SHA-256 is aa and the release says bb. Nothing was installed.' })
    expect(html({ state: failed })).toContain('does not match SHA256SUMS')
    expect(find(tree({ state: failed }), 'export-install')).toBeDefined()
    expect(html({ refusal: 'This call needs the admin role.' })).toContain('needs the admin role')
  })

  it('says a finished install needs a restart', () => {
    const done = state({ installed: true, whole: true, configured: true, release: '1', chromium: '156.0', summary: 'PDF export is installed.' }, { state: 'done', message: 'Release 1 is installed. Restart the gateway to use it.', restart_needed: true })
    expect(find(tree({ state: done }), 'export-done')!.props['data-restart']).toBe('true')
    expect(html({ state: done })).toContain('Restart the gateway')
    expect(find(tree({ state: done }), 'export-install')).toBeUndefined()
  })

  it('shows what is installed when it is whole, and lists the problems of one that is not, with a repair that forces the install', () => {
    const whole = state({ installed: true, whole: true, configured: true, release: '2026.11.12.1000', chromium: '156.0.8078.47', fonts: '5.2', summary: 'PDF export is installed: release 2026.11.12.1000, Chromium 156.0.8078.47.' })
    expect(html({ state: whole })).toContain('Chromium 156.0.8078.47')
    expect(find(tree({ state: whole }), 'export-component')!.props['data-state']).toBe('ready')
    expect(find(tree({ state: whole }), 'export-install')).toBeUndefined()

    const onInstall = vi.fn()
    const broken = state({ installed: true, whole: false, problems: ['the print file /x/print/print.css does not match export-component.json (changed after staging)'], summary: 'The installed component is not whole' })
    expect(html({ state: broken })).toContain('print.css')
    expect(find(tree({ state: broken }), 'export-component')!.props['data-state']).toBe('broken')
    ;(find(tree({ state: broken, onInstall }), 'export-install')!.props.onClick as () => void)()
    expect(onInstall).toHaveBeenCalledWith(true)
  })

  it('says so on a platform without a pinned browser and offers no install', () => {
    const none = state({ platform: null, supported: false, summary: 'No browser is pinned for this platform' })
    expect(find(tree({ state: none }), 'export-unsupported')).toBeDefined()
    expect(find(tree({ state: none }), 'export-install')).toBeUndefined()
  })

  it('shows a read that failed', () => {
    expect(html({ state: undefined, loadError: 'The management API said no' })).toContain('The management API said no')
  })

  it('says a component of the browser this program was tested with is verified, and one of another Chromium is installed with a warning, never a refusal', () => {
    const verified = state({ installed: true, whole: true, configured: true, release: '1', chromium: '156.0.8078.47', pins_verified: true, pins_note: 'verified: the browser and fonts this program was tested with (Chromium 156.0.8078.47, fonts 5.3.0).' })
    expect(find(tree({ state: verified }), 'export-pins-verified')).toBeDefined()
    expect(find(tree({ state: verified }), 'export-pins-warning')).toBeUndefined()
    const other = state({ installed: true, whole: true, configured: true, release: '2', chromium: '157.0.1.1', pins_verified: false, pins_note: 'this component carries Chromium 157.0.1.1 (this program was tested with 156.0.8078.47): installed as the release pins it.' })
    const shown = html({ state: other })
    expect(shown).toContain('Warning: this component carries Chromium 157.0.1.1 (this program was tested with 156.0.8078.47): installed as the release pins it.')
    expect(find(tree({ state: other }), 'export-component')!.props['data-state']).toBe('ready')   // a warning, not a broken state
    expect(find(tree({ state: other }), 'export-install')).toBeUndefined()
  })
})
