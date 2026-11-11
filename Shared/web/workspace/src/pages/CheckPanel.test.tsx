import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import type { CheckResult } from '../api'
import { checkLine, checkVerdict, publishesTo } from '../model'
import { CheckResultView } from './CheckPanel'

const broken: CheckResult = {
  kind: 'build', ok: false,
  diagnostics: [
    { severity: 'error', code: 'DDB-101', message: 'YAML syntax error: <bad>', file: 'models/marts/orders.yml', line: 29 },
    { severity: 'warning', code: 'DDB-210', message: 'No description.', file: null, line: null },
  ],
}

describe('a check of a draft', () => {
  it('says how many errors and warnings, in the same words the model gives', () => {
    expect(checkVerdict(broken).text).toBe('1 error and 1 warning to fix before this draft is merged or published.')
    expect(checkVerdict({ diagnostics: [] })).toEqual({ ok: true, text: 'The draft is sound.' })
    expect(checkVerdict({ diagnostics: [{ severity: 'warning' }, { severity: 'info' }] }).text).toBe('The draft is sound, with 1 warning.')
  })

  it('draws each finding with its code, and the file and line as a button that opens the file', () => {
    const html = renderToStaticMarkup(<CheckResultView result={broken} onOpen={() => {}} />)
    expect(html).toContain('data-ok="false"')
    expect(html.match(/data-testid="wsp-check-finding"/g)).toHaveLength(2)
    expect(html).toContain('data-code="DDB-101"')
    expect(html).toMatch(/<button[^>]*data-testid="wsp-check-open"[^>]*>models\/marts\/orders\.yml:29<\/button>/)
    expect(html).toContain('YAML syntax error: &lt;bad&gt;')       // escaped, never markup
    expect(html.match(/wsp-check-open/g)).toHaveLength(1)           // a finding with no place has no button
  })

  it('without a way to open files, a finding is one line of text', () => {
    expect(renderToStaticMarkup(<CheckResultView result={broken} />)).not.toContain('wsp-check-open')
    expect(checkLine(broken.diagnostics[0]!)).toBe('models/marts/orders.yml:29 DDB-101: YAML syntax error: <bad>')
    expect(checkLine(broken.diagnostics[1]!)).toBe('DDB-210: No description.')
  })

  it('a sound draft shows the verdict and no findings', () => {
    const html = renderToStaticMarkup(<CheckResultView result={{ kind: 'app', ok: true, diagnostics: [] }} />)
    expect(html).toContain('data-ok="true"')
    expect(html).toContain('The draft is sound.')
    expect(html).not.toContain('wsp-check-finding')
  })
})

describe('where a kind publishes', () => {
  it('names the product and what arrives there', () => {
    expect(publishesTo([])).toBe('nowhere yet')
    expect(publishesTo([{ product: 'gateway', what: 'app' }])).toBe('a Gateway (app)')
    expect(publishesTo([{ product: 'runner', what: 'package' }, { product: 'gateway', what: 'model' }])).toBe('a Runner (package) and a Gateway (model)')
  })
})
