import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { CheckView, DocumentsView, PublishViewsView, publishBlocker, viewDiagnostics, type DocumentsViewProps, type PublishViewsProps } from './ViewsTab'
import type { Diagnostic } from '../types'

const edge = { name: 'edge', url: 'https://gateway:8485', instance_id: 'gw-1', paired_at: '2026-10-30T10:00:00Z' }
const today = { kind: 'dashboard' as const, name: 'sales-today', path: 'dashboards/sales-today.yaml', hash: 'h1' }
const monthly = { kind: 'report' as const, name: 'monthly', path: 'reports/monthly.yaml', hash: 'h2' }

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const listProps = (over: Partial<DocumentsViewProps> = {}): DocumentsViewProps => ({ files: [today, monthly], loading: false, error: null, editHref: (f) => `/e/${f.kind}/${f.name}`, cannotMake: null, newHref: (k) => `/n/${k}`, ...over })
const list = (over?: Partial<DocumentsViewProps>) => renderToStaticMarkup(<MemoryRouter><DocumentsView {...listProps(over)} /></MemoryRouter>)

const publishProps = (over: Partial<PublishViewsProps> = {}): PublishViewsProps => ({ documents: [today, monthly], loading: false, error: null, proxies: [edge], proxy: 'edge', canPublish: true, busy: null, done: {}, failed: {}, onProxy: () => {}, onPublish: () => {}, ...over })
const publish = (over?: Partial<PublishViewsProps>) => renderToStaticMarkup(<PublishViewsView {...publishProps(over)} />)

const finding = (file: string, over: Partial<Diagnostic> = {}): Diagnostic => ({ code: 'DDB-106', severity: 'error', title: 'Invalid value', location: { file, line: 12, column: 5 }, found: 'unknown_member: sales.nope is not a catalog member.', supported: '', fix: '', ...over })

describe('the documents of a workspace', () => {
  it('lists each file with its kind, name and path and a link that edits it, and offers a new dashboard and a new report', () => {
    const text = list()
    expect(text).toContain('data-testid="build-views-doc-dashboard-sales-today"')
    expect(text).toContain('dashboards/sales-today.yaml')
    expect(text).toContain('href="/e/report/monthly"')
    expect(text).toContain('href="/n/dashboard"')
    expect(text).toContain('href="/n/report"')
  })

  it('says it is empty, with where the files go, and loading and error are not the empty message', () => {
    expect(list({ files: [] })).toContain('data-testid="build-views-empty"')
    expect(list({ files: [], loading: true })).toContain('data-testid="build-views-loading"')
    expect(list({ files: [], loading: true })).not.toContain('build-views-empty')
    expect(list({ files: [], error: new Error('x') })).toContain('data-testid="build-views-list-error"')
    expect(list({ files: [], error: new Error('x') })).not.toContain('build-views-empty')
  })

  it('has the new buttons disabled with the reason when the person may not author', () => {
    const text = list({ cannotMake: 'Saving needs the author role on this server.' })
    expect(text).toContain('data-testid="build-views-new-why"')
    expect(text).toMatch(/<button[^>]*disabled=""[^>]*title="Saving needs the author role on this server\."[^>]*data-testid="build-views-new-dashboard"/)
    expect(text).not.toContain('href="/n/dashboard"')
  })
})

describe('publishing documents of the project', () => {
  it('names the first thing that is missing, in the order a person can fix it: the role, a paired Gateway, a choice', () => {
    expect(publishBlocker({ proxies: [edge], proxy: 'edge', canPublish: true })).toBeNull()
    expect(publishBlocker({ proxies: [edge], proxy: 'edge', canPublish: false })).toContain('approver role')
    expect(publishBlocker({ proxies: [], proxy: '', canPublish: true })).toContain('not paired with a Gateway')
    expect(publishBlocker({ proxies: undefined, proxy: '', canPublish: true })).toContain('not paired with a Gateway')
    expect(publishBlocker({ proxies: [edge], proxy: '', canPublish: true })).toContain('Choose the Gateway')
  })

  it('has every button disabled and says why when the person is no approver, when no Gateway is paired and until one is chosen', () => {
    for (const [over, why] of [[{ canPublish: false }, 'approver role'], [{ proxies: [], proxy: '' }, 'not paired'], [{ proxy: '' }, 'Choose the Gateway']] as const) {
      const view = publishProps(over)
      expect(find(PublishViewsView(view), 'build-views-publish-button-dashboard-sales-today')!.props.disabled).toBe(true)
      expect(publish(over)).toContain(why)
    }
    expect(find(PublishViewsView(publishProps()), 'build-views-publish-button-dashboard-sales-today')!.props.disabled).toBe(false)
    expect(find(PublishViewsView(publishProps()), 'build-views-publish-why')).toBeUndefined()
  })

  it('presses publish with the document it belongs to, and no button works while one publish is under way', () => {
    const onPublish = vi.fn()
    ;(find(PublishViewsView(publishProps({ onPublish })), 'build-views-publish-button-report-monthly')!.props.onClick as () => void)()
    expect(onPublish).toHaveBeenCalledWith(monthly)
    const busy = publishProps({ busy: 'dashboard-sales-today' })
    expect(find(PublishViewsView(busy), 'build-views-publish-button-report-monthly')!.props.disabled).toBe(true)
    expect(publish({ busy: 'dashboard-sales-today' })).toContain('Publishing…')
  })

  it('shows what the Gateway answered under the document: the version, whether it is new, what it depends on', () => {
    const text = publish({ done: { 'dashboard-sales-today': { proxy: 'edge', kind: 'dashboard', name: 'sales-today', version: '3', created: true, unchanged: false, depends_on: ['semantic-model:retail_sales@2'] } } })
    expect(text).toContain('data-testid="build-views-publish-done-dashboard-sales-today"')
    expect(text).toContain('version <span class="mono">3</span> (new)')
    expect(text).toContain('semantic-model:retail_sales@2')
  })

  it('shows a refusal in the server\'s own words under the document it is about, and a 403 as the missing role', () => {
    const text = publish({ failed: { 'dashboard-sales-today': new ApiError(409, 'The proxy refused publishing the dashboard: the current version is 4.', { error: 'x' }) } })
    expect(text).toContain('data-testid="build-views-publish-failed-dashboard-sales-today"')
    expect(text).toContain('the current version is 4')
    expect(publish({ failed: { 'report-monthly': new ApiError(403, 'This needs the approver role.', {}) } })).toContain('Publishing needs the approver role')
  })

  it('says there is nothing to publish, loading, and an unreadable project, each apart', () => {
    expect(publish({ documents: [] })).toContain('data-testid="build-views-publish-none"')
    expect(publish({ documents: [], loading: true })).not.toContain('build-views-publish-none')
    expect(publish({ documents: [], error: new Error('x') })).toContain('data-testid="build-views-publish-error"')
    expect(publish({ documents: [], error: new Error('x') })).not.toContain('build-views-publish-none')
  })
})

describe('the project check as the Views tab shows it', () => {
  const check = (diagnostics: Diagnostic[], over: { checked?: boolean; pending?: boolean } = {}) =>
    renderToStaticMarkup(<MemoryRouter><CheckView diagnostics={diagnostics} checked={over.checked ?? true} pending={over.pending ?? false} editHref={(f) => (f === 'dashboards/a.yaml' ? '/edit/a' : null)} /></MemoryRouter>)

  it('keeps the findings about dashboards and reports, and no note, no model and no rendered file', () => {
    const all = [finding('dashboards/a.yaml'), finding('reports/b.yaml', { severity: 'warning' }), finding('models/x.sql'), finding('rendered/x.sql'), finding('dashboards/a.yaml', { severity: 'note' })]
    expect(viewDiagnostics(all).map((d) => d.location.file)).toEqual(['dashboards/a.yaml', 'reports/b.yaml'])
  })

  it('shows code, file and line, and links a finding to its document when it is in the workspace', () => {
    const text = check([finding('dashboards/a.yaml'), finding('reports/b.yaml', { severity: 'warning' })])
    expect(text).toContain('DDB-106')
    expect(text).toContain('href="/edit/a"')
    expect(text).toContain('dashboards/a.yaml:12')
    expect(text).toContain('unknown_member: sales.nope')
    expect(text).toContain('data-severity="warning"')
  })

  it('says not checked, checking, and clean, each apart', () => {
    expect(check([], { checked: false })).toContain('data-testid="build-views-check-idle"')
    expect(check([], { checked: false, pending: true })).toContain('data-testid="build-views-check-pending"')
    expect(check([finding('models/x.sql')])).toContain('data-testid="build-views-check-clean"')
  })
})
