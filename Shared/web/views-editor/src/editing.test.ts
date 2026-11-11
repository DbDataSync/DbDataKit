import { describe, expect, it } from 'vitest'
import type { ViewsDoc } from '@dbdatakit/visuals'
import { CUSTOM_FORMAT, formatChoice, modelOf, newWidget } from './editing'

const empty = { schemaVersion: 1, kind: 'dashboard', title: 'T', widgets: {}, layouts: [] } as unknown as ViewsDoc

describe('the model a new widget asks of', () => {
  it('is the host\'s default when the document has no query yet, and the retail sample\'s when there is no host default', () => {
    expect(modelOf(empty, 'orders')).toBe('orders')
    expect(modelOf(empty)).toBe('retail_sales')
    expect((newWidget('metric', empty, 'orders') as { query: { model: string } }).query.model).toBe('orders')
  })

  it('is the model the document\'s widgets already query, whatever the default', () => {
    const doc = { ...empty, widgets: { a: { type: 'metric', label: 'A', encodings: { value: 'x.y' }, query: { model: 'sales' } } } } as unknown as ViewsDoc
    expect(modelOf(doc, 'orders')).toBe('sales')
  })
})

describe('the format select', () => {
  it('shows a preset by its name, none as empty, and a mapping as the read-only custom choice', () => {
    expect(formatChoice('compact')).toBe('compact')
    expect(formatChoice(undefined)).toBe('')
    expect(formatChoice({ style: 'currency', currency: 'EUR' })).toBe(CUSTOM_FORMAT)
    expect(formatChoice({ date: 'month-year' })).toBe(CUSTOM_FORMAT)
    expect(formatChoice('yyyy')).toBe(CUSTOM_FORMAT)
  })
})
