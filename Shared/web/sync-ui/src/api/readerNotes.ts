import type { ReaderCapability } from './types'

/**
 * The short notes shown beside a reader's kind, from what the driver says the reader does rather than from its name, so a reader added later
 * describes itself with no change here.
 */
export function readerNotes(reader: ReaderCapability): string[] {
  const notes: string[] = []
  if (reader.supportsSegmentation) notes.push('segmentable')
  // Said, not prevented: a reader that cannot see deletes serves an append-only table well, and the person knows which kind of table this is.
  if (!reader.detectsDeletes) notes.push('does not detect deletes')
  // Absent means the driver did not say, which is read as yes: only an explicit false is a note.
  if (reader.detectsKeyChanges === false) notes.push('cannot see a key change')
  return notes
}
