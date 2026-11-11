import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { ChecksTable, FaultList } from './ReplicationChecksPanel'

const noop = () => {}

describe('the list of checks', () => {
  it('says there are none', () => {
    expect(renderToStaticMarkup(<ChecksTable checks={[]} canEdit onEdit={noop} onRemove={noop} />)).toContain('data-testid="replication-checks-empty"')
  })

  it('has a row for each check with its kind and severity, and edit and remove buttons for an administrator', () => {
    const html = renderToStaticMarkup(<ChecksTable canEdit onEdit={noop} onRemove={noop} checks={[
      { id: 'orders_fresh', kind: 'freshness', dataset: 'dbo.orders', max_age: '10m', severity: 'block', on_error: 'alert' },
      { id: 'name_present', kind: 'not_null', dataset: 'dbo.orders', column: 'Name', severity: 'warn', on_error: 'block' },
    ]} />)

    expect(html).toContain('data-testid="replication-check-orders_fresh" data-kind="freshness" data-severity="block"')
    expect(html).toContain('dbo.orders: no older than 10m')
    expect(html).toContain('blocks on error')
    expect(html).toContain('data-testid="replication-check-edit-name_present"')
    expect(html).toContain('data-testid="replication-check-remove-name_present"')
    expect(html).not.toContain('disabled')
  })

  it('disables edit and remove for a viewer and says why in the button\'s title', () => {
    const html = renderToStaticMarkup(<ChecksTable canEdit={false} onEdit={noop} onRemove={noop} checks={[{ id: 'a', kind: 'unique', dataset: 'd', column: 'k', severity: 'warn' }]} />)

    expect(html.match(/disabled/g)).toHaveLength(2)
    expect(html).toContain('Editing a check needs the admin role.')
    expect(html).toContain('Removing a check needs the admin role.')
  })
})

describe('the faults of a refused save', () => {
  it('give the file and line, the reader\'s message and what to do', () => {
    const html = renderToStaticMarkup(<FaultList testId="replication-checks-diagnostics" intro="The checks were not saved." faults={[
      { code: 'DBD-QLT-001', file: 'task.yaml', line: 7, column: 11, message: '`freshnes` is not a kind of check. Did you mean `freshness`?', fix: 'The kinds are `freshness`, `not_null`' },
    ]} />)

    expect(html).toContain('task.yaml:7')
    expect(html).toContain('Did you mean `freshness`?')
    expect(html).toContain('(The kinds are')
    expect(html).toContain('data-code="DBD-QLT-001"')
  })
})
