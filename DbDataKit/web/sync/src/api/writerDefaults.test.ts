import { describe, expect, it } from 'vitest'
import { CHANGE_WRITER_KIND, writerForReader, writerNote } from './types'
import type { ReaderCapability, WriterCapability } from './types'

const reader = (kind: string, intents: string[]): ReaderCapability =>
  ({ kind, parameters: [], supportsSegmentation: false, detectsDeletes: false, supportedIntents: intents }) as unknown as ReaderCapability
const writer = (kind: string, supportsReconciliation: boolean): WriterCapability => ({ kind, parameters: [], supportsReconciliation })

const readers = [reader('BatchReload', []), reader('MsSqlChangeTracking', ['Changes', 'ChangesFromEarliest']), reader('Watermark', ['Changes'])]
const writers = [writer('DeleteInsert', true), writer('ChangeApply', false), writer('MsSqlMerge', false), writer('Snapshot', false)]

describe('the writer offered when the reader changes (CW01)', () => {
  it('is the change writer when a reader that reads changes meets a reconciling writer', () => {
    expect(writerForReader('MsSqlChangeTracking', readers, writers, 'DeleteInsert')).toBe(CHANGE_WRITER_KIND)
    expect(writerForReader('Watermark', readers, writers, 'DeleteInsert')).toBe('ChangeApply')
  })

  it('leaves a writer that does not reconcile alone: an operator who chose MsSqlMerge or Snapshot keeps it', () => {
    expect(writerForReader('MsSqlChangeTracking', readers, writers, 'MsSqlMerge')).toBeNull()
    expect(writerForReader('MsSqlChangeTracking', readers, writers, 'Snapshot')).toBeNull()
    expect(writerForReader('MsSqlChangeTracking', readers, writers, 'ChangeApply')).toBeNull()
  })

  it('leaves the writer alone behind a reload reader', () => {
    expect(writerForReader('BatchReload', readers, writers, 'DeleteInsert')).toBeNull()
  })

  it('offers nothing a target does not have', () => {
    expect(writerForReader('MsSqlChangeTracking', readers, writers.filter((w) => w.kind !== 'ChangeApply'), 'DeleteInsert')).toBeNull()
    expect(writerForReader('Unknown', readers, writers, 'DeleteInsert')).toBeNull()
  })
})

describe('what the pickers say next to a writer', () => {
  it('names the change writer, and keeps the old words for the rest', () => {
    expect(writerNote(writer('ChangeApply', false))).toBe('change writer, default')
    expect(writerNote(writer('MsSqlMerge', false))).toBe('upsert-only')
    expect(writerNote(writer('DeleteInsert', true))).toBe('reconciling')
  })
})
