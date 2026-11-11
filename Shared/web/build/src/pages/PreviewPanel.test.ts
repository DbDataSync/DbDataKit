import { describe, expect, it } from 'vitest'
import { isModelQuery, renderedLabel } from './PreviewPanel'

describe('PreviewPanel', () => {
  it('previews only the query files of models', () => {
    expect(isModelQuery('models/staging/orders.sql')).toBe(true)
    expect(isModelQuery('models/staging/orders.yml')).toBe(false)
    expect(isModelQuery('seeds/countries.sql')).toBe(false)
  })
  it('names a rendered file by its connection and file', () => {
    expect(renderedLabel('rendered/lowered/staging.orders/lowered.sql')).toBe('lowered · lowered.sql')
    expect(renderedLabel('rendered/pg/staging.orders/load.sql')).toBe('pg · load.sql')
  })
})
