import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { StoreError } from './types'
import { FormView, GridView, HistoryView, ViolationList } from './views'
import { territory } from './testing'

const rows = [
  { values: { code: 'EU', name: 'Europe', tier: 'gold', active: true, quota: 10.5 }, version: 1, valid_from: null, valid_to: null },
  { values: { code: 'NA', name: 'North <b>', tier: null, active: false, quota: null }, version: 2, valid_from: null, valid_to: null },
]

describe('the grid', () => {
  it('shows a column for each attribute, a row for each row, and escapes what is in them', () => {
    const html = renderToStaticMarkup(<GridView entity={territory} rows={rows} offset={0} limit={25} />)
    expect(html.match(/<th /g)).toHaveLength(5)
    expect(html.match(/<tr /g)).toHaveLength(2)
    expect(html).toContain('North &lt;b&gt;')
    expect(html).toContain('yes')
    expect(html).toContain('1–2')
  })

  it('marks the selected row, and can show chosen columns only', () => {
    const html = renderToStaticMarkup(<GridView entity={territory} rows={rows} offset={0} limit={25} selected={{ code: 'NA' }} columns={['code', 'name']} />)
    expect(html.match(/<th /g)).toHaveLength(2)
    expect(html).toMatch(/aria-selected="true"[^>]*class="sui-selected"/)
  })

  it('says so when there are no rows, and the pager follows the page size', () => {
    expect(renderToStaticMarkup(<GridView entity={territory} rows={[]} offset={0} limit={25} />)).toContain('No rows.')
    const full = renderToStaticMarkup(<GridView entity={territory} rows={rows} offset={2} limit={2} onPage={() => {}} />)
    expect(full).not.toMatch(/<button[^>]*disabled[^>]*>Next/)
    expect(full).not.toMatch(/<button[^>]*disabled[^>]*>Previous/)
  })
})

describe('the form', () => {
  const text = { code: '', name: '', tier: '', active: '', quota: '' }

  it('a new row has every attribute, with the right control for each', () => {
    const html = renderToStaticMarkup(<FormView entity={territory} row={null} text={text} />)
    expect(html).toContain('New territory')
    expect(html).toContain('type="checkbox"')
    expect(html).toContain('<select')
    expect(html).toContain('type="number"')
    expect(html).toContain('maxLength="10"')
    expect(html).toContain('required')
    expect(html).toContain('>Create<')
  })

  it('an existing row shows its key fixed and offers Save and Delete', () => {
    const html = renderToStaticMarkup(<FormView entity={territory} row={rows[0]!} text={{ ...text, name: 'Europe' }} onDelete={() => {}} />)
    expect(html).toContain('Territory EU')
    expect(html).toMatch(/id="sui-code"[^>]*disabled/)
    expect(html).toContain('>Save<')
    expect(html).toContain('>Delete<')
  })

  it('a server refusal is shown against its attribute, and a stale version says to reload', () => {
    const violations = [{ rule: 'min', entity: 'territory', attribute: 'quota', key: {}, message: 'must be at least 0' }, { rule: 'sum_equals', entity: 'territory', attribute: null, key: {}, message: 'the quotas must add up' }]
    const html = renderToStaticMarkup(<FormView entity={territory} row={null} text={text} problems={{ quota: 'must be at least 0' }} violations={violations} />)
    expect(html).toContain('sui-invalid')
    expect(html.match(/must be at least 0/g)).toHaveLength(1)
    expect(html).toContain('the quotas must add up')
    const stale = renderToStaticMarkup(<FormView entity={territory} row={rows[0]!} text={text} error={new StoreError('The row changed.', 409, [], 5)} />)
    expect(stale).toContain('Reload the row')
  })

  it('read only has no buttons and disabled controls', () => {
    const html = renderToStaticMarkup(<FormView entity={territory} row={rows[0]!} text={text} readOnly />)
    expect(html).not.toContain('<button')
    expect(html).toMatch(/id="sui-name"[^>]*disabled/)
  })
})

it('violations list the rule, the attribute and the message', () => {
  const html = renderToStaticMarkup(<ViolationList violations={[{ rule: 'pattern', entity: 'e', attribute: 'code', key: {}, message: 'wrong shape' }]} />)
  expect(html).toContain('pattern')
  expect(html).toContain('code: ')
  expect(html).toContain('wrong shape')
  expect(renderToStaticMarkup(<ViolationList violations={[]} />)).toBe('')
})

it('history lists each version with the period it held', () => {
  const html = renderToStaticMarkup(<HistoryView entity={territory} versions={[{ ...rows[0]!, valid_from: '2026-01-01T00:00:00Z', valid_to: null }]} />)
  expect(html).toContain('2026-01-01')
  expect(html).toContain('current')
})
