/**
 * Refreshing a dashboard on an interval (plan 32 section 3.5 item 4, DBR08). Everything that touches time goes through a `Clock` the caller gives, so a test drives it with `fakeClock` and no real timer or
 * wall-clock reading is involved. **Limits** (also in docs/views.md): the interval is never below the floor (15 s, the server's setting; a shorter `every` is raised to it); a random share of up to 10 % is added
 * to each wait (so many open boards do not ask at the same second; never subtracted, so the floor holds); nothing is requested while the page is hidden, and when it shows again a refresh that came due is made at once;
 * a refresh is never started while the last one is still running; a failed refresh keeps the rows already shown and marks them stale until the next one succeeds.
 */

export interface Clock {
  now(): number
  setTimeout(callback: () => void, ms: number): unknown
  clearTimeout(handle: unknown): void
}

export const systemClock: Clock = {
  now: () => Date.now(),
  setTimeout: (callback, ms) => globalThis.setTimeout(callback, ms),
  clearTimeout: (handle) => globalThis.clearTimeout(handle as ReturnType<typeof globalThis.setTimeout>),
}

/** The server's lowest interval (a setting of the serving module; 15 s is its default). */
export const REFRESH_FLOOR_MS = 15_000
/** The largest share of the wait added at random. */
export const REFRESH_JITTER = 0.1

const UNITS: Record<string, number> = { s: 1000, m: 60_000, h: 3_600_000 }

/** `30s`, `5m`, `1h` as milliseconds; anything else (zero, a fraction, no unit) is null. */
export function parseEvery(text: unknown): number | null {
  if (typeof text !== 'string') return null
  const m = /^(\d+)([smh])$/.exec(text.trim())
  if (!m) return null
  const ms = Number(m[1]) * UNITS[m[2]!]!
  return ms > 0 ? ms : null
}

/** How long to wait: the interval, raised to the floor, plus up to `REFRESH_JITTER` of it (`random` gives [0, 1)). Whole milliseconds, never below the floor. */
export function refreshDelay(everyMs: number, options: { floorMs?: number; random?: () => number } = {}): number {
  const floor = options.floorMs ?? REFRESH_FLOOR_MS
  const base = Math.max(everyMs, floor)
  const share = Math.min(Math.max((options.random ?? Math.random)(), 0), 0.999999)
  return Math.round(base * (1 + REFRESH_JITTER * share))
}

export interface RefreshState {
  /** A refresh is in flight. */
  running: boolean
  /** Waiting for the page to be shown again. */
  paused: boolean
  /** The last refresh failed: the rows shown are older than they should be. */
  stale: boolean
  error: string | null
  /** When the last refresh ended (the clock's reading), and how many have run. */
  lastRun: number | null
  runs: number
}

export interface RefresherOptions {
  everyMs: number
  floorMs?: number
  clock: Clock
  random?: () => number
  hidden?: boolean
  /** One refresh; a rejection marks the rows stale. */
  run: () => Promise<void>
  onChange?: (state: RefreshState) => void
}

export interface Refresher {
  start(): void
  stop(): void
  /** The page was hidden or shown. */
  setHidden(hidden: boolean): void
  readonly state: RefreshState
}

export function createRefresher(options: RefresherOptions): Refresher {
  const { clock, run } = options
  let hidden = options.hidden ?? false
  let stopped = true
  let timer: unknown = null
  let dueAt = 0
  let state: RefreshState = { running: false, paused: false, stale: false, error: null, lastRun: null, runs: 0 }
  const set = (patch: Partial<RefreshState>) => {
    state = { ...state, ...patch }
    options.onChange?.(state)
  }
  const cancel = () => {
    if (timer !== null) clock.clearTimeout(timer)
    timer = null
  }
  const wait = (ms: number) => {
    cancel()
    dueAt = clock.now() + ms
    timer = clock.setTimeout(fire, ms)
  }
  const schedule = () => {
    if (hidden) {
      // the next one is due a full wait from now; it is made when the page shows again, if by then it is due
      dueAt = clock.now() + refreshDelay(options.everyMs, options)
      set({ paused: true })
    } else wait(refreshDelay(options.everyMs, options))
  }
  async function execute() {
    cancel()
    set({ running: true, paused: false })
    try {
      await run()
      set({ stale: false, error: null })
    } catch (e) {
      set({ stale: true, error: e instanceof Error ? e.message : String(e) })
    }
    set({ running: false, lastRun: clock.now(), runs: state.runs + 1 })
    if (!stopped) schedule()
  }
  function fire() {
    timer = null
    if (stopped) return
    if (hidden) set({ paused: true })
    else void execute()
  }
  return {
    start() {
      if (!stopped) return
      stopped = false
      schedule()
    },
    stop() {
      stopped = true
      cancel()
    },
    setHidden(next) {
      if (next === hidden) return
      hidden = next
      if (stopped) return
      if (hidden) {
        cancel()
        set({ paused: true })
      } else if (!state.running) {
        set({ paused: false })
        if (clock.now() >= dueAt) void execute()
        else wait(dueAt - clock.now())
      }
    },
    get state() {
      return state
    },
  }
}

/** `14:05` from an ISO 8601 instant, in the viewer's time zone (or the one named: tests say `UTC`). Empty for text that is not a time. */
export function formatAsOf(iso: string | null | undefined, timeZone?: string): string {
  if (!iso) return ''
  const t = Date.parse(iso)
  if (Number.isNaN(t)) return ''
  return new Intl.DateTimeFormat('en-GB', { hour: '2-digit', minute: '2-digit', hour12: false, timeZone }).format(t)
}

/** Whether the page is shown: the browser's `visibilitychange`, or whatever a test gives. */
export interface Visibility {
  hidden(): boolean
  subscribe(listener: () => void): () => void
}

export const browserVisibility: Visibility = {
  hidden: () => typeof document !== 'undefined' && document.visibilityState === 'hidden',
  subscribe(listener) {
    if (typeof document === 'undefined') return () => {}
    document.addEventListener('visibilitychange', listener)
    return () => document.removeEventListener('visibilitychange', listener)
  },
}
