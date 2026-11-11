/** One line of a run's output (`GET /api/v1/runs/{id}/logs`): its id orders the lines and pages the read. */
export interface RunLogLine { id: number; at: string; level: string; item: string | null; message: string }

/** The most lines a screen keeps of one run, so a run that writes for hours does not fill the page. */
export const MAX_LINES = 2000

/** The lines of a page that are not shown yet, added in order (a read again after a refresh gives some twice), the oldest dropped past {@link MAX_LINES}. */
export function appendLines(shown: RunLogLine[], page: RunLogLine[], max = MAX_LINES): RunLogLine[] {
  const last = shown.length === 0 ? -1 : shown[shown.length - 1]!.id
  const merged = [...shown, ...page.filter((l) => l.id > last)]
  return merged.length > max ? merged.slice(merged.length - max) : merged
}

/** Where the next read starts: after the last line seen, or from the start. */
export const lastId = (lines: RunLogLine[]): number | undefined => (lines.length === 0 ? undefined : lines[lines.length - 1]!.id)

/** A level as the runner records it (any case) as a word of the screen, and whether it is one a person looks at first. */
export const isProblem = (level: string) => /^(error|critical|fatal)/i.test(level)
export const isWarning = (level: string) => /^warn/i.test(level)

/** One line as plain text for copying: time, level, the item it was about, the message. */
export function lineText(line: RunLogLine): string {
  const time = new Date(line.at)
  const at = Number.isNaN(time.getTime()) ? line.at : time.toISOString().slice(11, 23)
  return `${at} ${line.level.toUpperCase().padEnd(5)} ${line.item ? `[${line.item}] ` : ''}${line.message}`
}

/** A line is shown in full up to this length (a run may write a long statement or a stack); the rest is cut with a mark and stays in the copy. */
export const SHOWN_LENGTH = 2000
export const shown = (message: string) => (message.length > SHOWN_LENGTH ? `${message.slice(0, SHOWN_LENGTH)}… (${message.length - SHOWN_LENGTH} more characters)` : message)
