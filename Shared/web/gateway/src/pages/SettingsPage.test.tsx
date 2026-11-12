import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import type { Diagnostic, SettingsCheck } from '../types'
import { SettingsView, type SettingsViewProps } from './SettingsPage'

const diag = (key: string | null, message = 'wrong', severity = 'error'): Diagnostic => ({ code: 'DBP-1', severity, key, file: '/x/config/server.yaml', line: 5, column: 1, message, fix: null })
const check = (over: Partial<SettingsCheck> = {}): SettingsCheck => ({ valid: true, diagnostics: [], fields: { modules: ['flightsql'], 'views.cacheSeconds': 30, logSql: true }, changed: ['logSql'], restart_keys: [], problems: [], ...over })

function props(over: Partial<SettingsViewProps> = {}): SettingsViewProps {
  return {
    loading: false, loadError: null, text: 'schemaVersion: 2\n', dirty: true, masked: false, tab: 'form', check: check(), checking: false, checkFailed: false, allowed: true, busy: false, note: '', saved: null, refusal: null, stale: false, cacheText: null,
    onTab: () => {}, onText: () => {}, onField: () => {}, onCacheText: () => {}, onNote: () => {}, onSave: () => {}, onReload: () => {}, ...over,
  }
}

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const tree = (over?: Partial<SettingsViewProps>) => SettingsView(props(over))
const html = (over?: Partial<SettingsViewProps>) => renderToStaticMarkup(<MemoryRouter>{tree(over)}</MemoryRouter>)

describe('the server settings screen', () => {
  it('shows the modules and the fields as the draft sets them', () => {
    const text = html({ check: check({ fields: { modules: ['flightsql', 'views'], 'views.cacheSeconds': 45, 'views.export.mode': 'system', 'views.export.path': '/opt/chrome', logSql: false } }) })
    expect(find(tree({ check: check({ fields: { modules: ['flightsql', 'views'] } }) }), 'settings-module-views')!.props.checked).toBe(true)
    expect(find(tree(), 'settings-module-views')!.props.checked).toBe(false)
    expect(text).toContain('value="45"')
    expect(find(tree({ check: check({ fields: { 'views.export.mode': 'system' } }) }), 'settings-export-mode')!.props.value).toBe('system')
    expect(find(tree({ check: check({ fields: { logSql: false } }) }), 'settings-log-sql')!.props.checked).toBe(false)
  })

  it('shows the telemetry fields as the draft sets them, sends a change, and leaves the collector alone while it is off (plan 22a GP28)', () => {
    const onField = vi.fn()
    expect(find(tree(), 'settings-telemetry-enabled')!.props.checked).toBe(false)
    expect(find(tree(), 'settings-telemetry-endpoint')!.props.disabled).toBe(true)
    const on = check({ fields: { 'telemetry.enabled': true, 'telemetry.endpoint': 'http://collector:4317', 'telemetry.protocol': 'http' } })
    expect(find(tree({ check: on }), 'settings-telemetry-endpoint')!.props.value).toBe('http://collector:4317')
    expect(find(tree({ check: on }), 'settings-telemetry-endpoint')!.props.disabled).toBe(false)
    expect(find(tree({ check: on }), 'settings-telemetry-protocol')!.props.value).toBe('http')
    ;(find(tree({ onField }), 'settings-telemetry-enabled')!.props.onChange as (e: { target: { checked: boolean } }) => void)({ target: { checked: true } })
    expect(onField).toHaveBeenCalledWith('telemetry.enabled', true)
    ;(find(tree({ onField, check: on }), 'settings-telemetry-endpoint')!.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: 'http://x:4317' } })
    expect(onField).toHaveBeenCalledWith('telemetry.endpoint', 'http://x:4317')
    expect(html()).toContain('never holds a secret')
  })

  it('sends a change of a module as the list it would give, in the order of the known modules', () => {
    const onField = vi.fn()
    ;(find(tree({ onField, check: check({ fields: { modules: ['flightsql'] } }) }), 'settings-module-views')!.props.onChange as (e: { target: { checked: boolean } }) => void)({ target: { checked: true } })
    expect(onField).toHaveBeenCalledWith('modules', ['flightsql', 'views'])
  })

  it('sends a cache value only when it is a whole number in range, and says so when it is not', () => {
    const onField = vi.fn(); const onCacheText = vi.fn()
    const change = (value: string) => (find(tree({ onField, onCacheText }), 'settings-cache-seconds')!.props.onChange as (e: { target: { value: string } }) => void)({ target: { value } })
    change('60')
    expect(onField).toHaveBeenCalledWith('views.cacheSeconds', 60)
    onField.mockClear()
    change('99999')
    expect(onField).not.toHaveBeenCalled()
    expect(onCacheText).toHaveBeenCalledWith('99999')
    expect(html({ cacheText: '99999' })).toContain('settings-cache-problem')
    expect(html({ cacheText: '60' })).not.toContain('settings-cache-problem')
  })

  it('sends a semantic number only when it is a whole number in its range, keeps what was typed, and shows the Gateway\'s error beside the field', () => {
    const onField = vi.fn(); const onSemanticText = vi.fn()
    const change = (id: string, value: string) => (find(tree({ onField, onSemanticText }), id)!.props.onChange as (e: { target: { value: string } }) => void)({ target: { value } })
    change('settings-semantic-combine-memoryMb', '512')
    expect(onField).toHaveBeenCalledWith('semantic.combine.memoryMb', 512)
    onField.mockClear()
    change('settings-semantic-combine-memoryMb', '4')
    expect(onField).not.toHaveBeenCalled()
    expect(onSemanticText).toHaveBeenCalledWith('semantic.combine.memoryMb', '4')
    expect(html({ semanticText: { 'semantic.combine.memoryMb': '4' } })).toContain('settings-semantic-combine-memoryMb-problem')
    expect(html()).toContain('value="1024"')
    expect(html({ check: check({ valid: false, diagnostics: [diag('Semantic.Level', 'Value 9 is above the maximum 5.')] }) })).toContain('Value 9 is above the maximum 5.')
    expect(html()).toContain('server.yaml tab')
  })

  it('shows the validator\'s errors beside the field they belong to, and the rest with the file and line', () => {
    const text = html({ check: check({ valid: false, diagnostics: [diag('Views.CacheSeconds', 'Value 99999 is above the maximum 3600.'), diag('Tls.Mode', 'Not a mode.')] }) })
    expect(text).toContain('data-testid="settings-error-views.cacheSeconds"')
    expect(text).toContain('Value 99999 is above the maximum 3600.')
    expect(text).toContain('data-testid="settings-diagnostic"')
    expect(text).toContain('Not a mode.')
    expect(text).toContain('server.yaml:5')
  })

  it('says a restart is needed before saving and after', () => {
    expect(html({ check: check({ restart_keys: ['modules'] }) })).toContain('modules is read when the Gateway starts')
    const text = html({ saved: { written: { version: 'v', changed: ['modules'], restart_keys: ['modules'], restart_needed: true, commit: 'abcdef1234' }, text: '' } })
    expect(text).toContain('data-restart="true"')
    expect(text).toContain('restart the Gateway for it to take effect')
    expect(text).toContain('abcdef1')
    expect(html({ saved: { written: { version: 'v', changed: ['logSql'], restart_keys: [], restart_needed: false, commit: 'abcdef1234' }, text: '' } })).toContain('data-restart="false"')
  })

  it('turns Save on for a changed valid draft, and says why it is off otherwise', () => {
    expect(find(tree(), 'settings-save')!.props.disabled).toBe(false)
    expect(renderToStaticMarkup(<>{tree({ dirty: false })}</>)).toContain('Nothing is changed yet.')
    expect(find(tree({ allowed: false }), 'settings-save')!.props.disabled).toBe(true)
    expect(find(tree({ check: check({ valid: false, diagnostics: [diag('Tls.Mode')] }) }), 'settings-save')!.props.title).toBe('Fix the 1 problem first.')
    expect(find(tree({ checking: true }), 'settings-save')!.props.disabled).toBe(true)
    expect(find(tree({ busy: true }), 'settings-save')!.props.disabled).toBe(true)
  })

  it('saves when the button is pressed', () => {
    const onSave = vi.fn()
    ;(find(tree({ onSave }), 'settings-save')!.props.onClick as () => void)()
    expect(onSave).toHaveBeenCalledTimes(1)
  })

  it('shows the YAML tab as an editable text, read-only without the role, and sends what is typed', () => {
    const onText = vi.fn()
    const yaml = find(tree({ tab: 'yaml', onText, text: 'a: 1\n' }), 'settings-yaml')!
    expect(yaml.props.value).toBe('a: 1\n')
    expect(yaml.props.readOnly).toBe(false)
    ;(yaml.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: 'a: 2\n' } })
    expect(onText).toHaveBeenCalledWith('a: 2\n')
    expect(find(tree({ tab: 'yaml', allowed: false }), 'settings-yaml')!.props.readOnly).toBe(true)
    expect(html({ tab: 'yaml' })).not.toContain('settings-form')
  })

  it('says the file changed since it was read and offers to read it again, keeping the draft', () => {
    const onReload = vi.fn()
    expect(html({ stale: true })).toContain('settings-conflict')
    ;(find(tree({ stale: true, onReload }), 'settings-reload')!.props.onClick as () => void)()
    expect(onReload).toHaveBeenCalled()
  })

  it('shows the Gateway\'s refusal and its diagnostics, loading, load errors and the masked-secret note', () => {
    const text = html({ refusal: { message: 'The change is not valid, so nothing was written.', diagnostics: [diag('Views.CacheSeconds', 'Too big')] } })
    expect(text).toContain('nothing was written')
    expect(text).toContain('Too big')
    expect(html({ loading: true })).toContain('settings-loading')
    expect(html({ loadError: 'This call needs the admin role.' })).toContain('could not be read: This call needs the admin role.')
    expect(html({ masked: true })).toContain('shown as &#x27;********&#x27;')
    expect(html({ masked: false })).not.toContain('settings-masked-note')
  })

  it('disables the form fields without the role', () => {
    for (const id of ['settings-module-views', 'settings-cache-seconds', 'settings-export-mode', 'settings-log-sql']) expect(find(tree({ allowed: false }), id)!.props.disabled, id).toBe(true)
  })
})
