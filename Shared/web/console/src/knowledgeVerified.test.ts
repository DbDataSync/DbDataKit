import { describe, expect, it } from 'vitest'
import { verifiedLine, verifiedQueryId, verifiedQueryText, verifiedRuns, type VerifiedPreview } from './knowledge'

// Plan 27a KN21: what the Knowledge page says about a verified query and how the form starts one. The server's strict reader decides what is valid; these are the words and the file the form writes.

const verified = (over: Partial<VerifiedPreview> = {}): VerifiedPreview => ({
  model: 'Sales', questions: ['What was revenue by region?'], request: '{}', semanticSql: null, savedQuery: null, savedQueryVersion: null, verifiedBy: 'maria', verifiedAt: '2026-11-06', modelVersion: '4', ...over,
})

describe('a verified query in words', () => {
  it('says who verified it, when, and against which version of the model', () => {
    expect(verifiedLine(verified())).toBe('Verified by maria on 2026-11-06 against version 4 of Sales.')
  })
  it('says what it runs: the pinned request, or the saved query of the model with its pinned version', () => {
    expect(verifiedRuns(verified())).toBe('the pinned request')
    expect(verifiedRuns(verified({ savedQuery: 'revenue_by_region', savedQueryVersion: '3' }))).toBe('the saved query Sales/revenue_by_region@3')
    expect(verifiedRuns(verified({ savedQuery: 'revenue_by_region' }))).toBe('the saved query Sales/revenue_by_region')
  })
})

describe('starting one from the form', () => {
  it('names the file from the question, in the queries folder', () => {
    expect(verifiedQueryId('What was revenue by region last quarter?')).toBe('queries/what-was-revenue-by-region-last-quarter.md')
    expect(verifiedQueryId('???')).toBe('queries/question.md')
  })
  it('writes a verified query the strict reader will read: kind, question, request as JSON text, and who verified it', () => {
    const text = verifiedQueryText({ question: 'Revenue  by\nregion?', model: ' Sales ', request: '{"kind":"query","query":{"measures":["revenue"],"limit":10}}', by: 'Maria', today: '2026-11-12', owner: 'finance-data' })
    expect(text).toContain('kind: verified-query')
    expect(text).toContain('model: Sales\n')
    expect(text).toContain('owner: finance-data\n')
    expect(text).toContain('  - "Revenue by region?"')
    expect(text).toContain('verified: { by: "Maria", at: 2026-11-12, model-version: "1" }')
    // the request travels as JSON text in one string, so quotes inside it cannot break the front matter
    const request = text.split('\n').find((l) => l.startsWith('request: '))!
    expect(JSON.parse(JSON.parse(request.slice('request: '.length)))).toEqual({ kind: 'query', query: { measures: ['revenue'], limit: 10 } })
  })
  it('starts from a request with one measure when none was given, for the person to change', () => {
    const text = verifiedQueryText({ question: 'q', model: 'Sales', request: '  ', by: 'a', today: '2026-11-12' })
    expect(text).not.toContain('owner:')
    expect(text).toContain('measures')
  })
})
