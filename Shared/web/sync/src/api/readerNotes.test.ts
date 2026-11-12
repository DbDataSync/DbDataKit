import { describe, expect, it } from 'vitest'
import { readerNotes } from './readerNotes'
import type { ReaderCapability } from './types'

const reader = (over: Partial<ReaderCapability>): ReaderCapability =>
  ({ kind: 'X', parameters: [], supportsSegmentation: false, detectsDeletes: true, supportedIntents: [], ...over }) as ReaderCapability

describe('the notes beside a reader (CW02)', () => {
  it('says a reader cannot see a key change only when the server says so', () => {
    expect(readerNotes(reader({ detectsKeyChanges: false }))).toContain('cannot see a key change')
    expect(readerNotes(reader({ detectsKeyChanges: true }))).not.toContain('cannot see a key change')
    // a server older than the capability does not send it: the reader could say it
    expect(readerNotes(reader({}))).not.toContain('cannot see a key change')
  })

  it('keeps the existing notes', () => {
    expect(readerNotes(reader({ detectsDeletes: false, supportsSegmentation: true, detectsKeyChanges: false })))
      .toEqual(['segmentable', 'does not detect deletes', 'cannot see a key change'])
  })
})
