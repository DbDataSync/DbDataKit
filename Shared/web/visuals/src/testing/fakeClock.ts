import type { Clock } from '../dashboard/refresh'

/** A clock the test drives: `advance(ms)` runs every callback that comes due, in order, at its own time; nothing here reads the real time or starts a real timer. */
export interface FakeClock extends Clock {
  advance(ms: number): void
  /** The callbacks waiting. */
  readonly pending: number
}

export function fakeClock(start = 0): FakeClock {
  let now = start
  let next = 1
  const waiting = new Map<number, { at: number; callback: () => void }>()
  return {
    now: () => now,
    setTimeout(callback, ms) {
      const id = next++
      waiting.set(id, { at: now + Math.max(0, ms), callback })
      return id
    },
    clearTimeout(handle) {
      waiting.delete(handle as number)
    },
    advance(ms) {
      const end = now + ms
      for (;;) {
        const due = [...waiting.entries()].filter(([, w]) => w.at <= end).sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0]
        if (!due) break
        waiting.delete(due[0])
        now = due[1].at
        due[1].callback()
      }
      now = end
    },
    get pending() {
      return waiting.size
    },
  }
}
