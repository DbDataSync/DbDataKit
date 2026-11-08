import { describe, expect, it } from 'vitest'
import { fakeClock } from '../testing/fakeClock'
import { REFRESH_FLOOR_MS, createRefresher, formatAsOf, parseEvery, refreshDelay, type RefreshState } from './refresh'

const flush = async () => { for (let i = 0; i < 5; i++) await Promise.resolve() }

describe('parseEvery', () => {
  it('reads seconds, minutes and hours, and nothing else', () => {
    expect(parseEvery('30s')).toBe(30_000)
    expect(parseEvery('5m')).toBe(300_000)
    expect(parseEvery('1h')).toBe(3_600_000)
    for (const bad of ['0s', '5', 'm', '1.5m', '-5m', '5 min', '', undefined, 5, null]) expect(parseEvery(bad as never), String(bad)).toBeNull()
  })
})

describe('refreshDelay', () => {
  it('is never below the floor, and only ever adds jitter', () => {
    expect(refreshDelay(1_000, { random: () => 0 })).toBe(REFRESH_FLOOR_MS)
    expect(refreshDelay(1_000, { random: () => 0.5 })).toBe(Math.round(REFRESH_FLOOR_MS * 1.05))
    expect(refreshDelay(60_000, { random: () => 0 })).toBe(60_000)
    expect(refreshDelay(60_000, { random: () => 0.999 })).toBeLessThanOrEqual(66_000)
    expect(refreshDelay(60_000, { random: () => 5 })).toBeLessThanOrEqual(66_000)
    expect(refreshDelay(60_000, { random: () => -1 })).toBe(60_000)
    expect(refreshDelay(1_000, { floorMs: 5_000, random: () => 0 })).toBe(5_000)
    // a sweep: no draw gives less than the floor or more than the jitter allows
    for (let i = 0; i < 100; i++) {
      const d = refreshDelay(20_000, { random: () => i / 100 })
      expect(d).toBeGreaterThanOrEqual(20_000)
      expect(d).toBeLessThanOrEqual(22_000)
    }
  })
})

function setup(over: { everyMs?: number; hidden?: boolean; fail?: () => boolean; floorMs?: number } = {}) {
  const clock = fakeClock(1_000_000)
  const states: RefreshState[] = []
  const log: number[] = []
  const release: Array<() => void> = []
  let hold = false
  const refresher = createRefresher({
    everyMs: over.everyMs ?? 60_000,
    floorMs: over.floorMs,
    clock,
    random: () => 0,
    hidden: over.hidden,
    onChange: (s) => states.push(s),
    run: async () => {
      log.push(clock.now())
      if (hold) await new Promise<void>((r) => release.push(r))
      if (over.fail?.()) throw new Error('the source did not answer')
    },
  })
  return { clock, refresher, log, states, hold: (v: boolean) => { hold = v }, release: () => release.splice(0).forEach((r) => r()) }
}

describe('createRefresher (fake clock, no timers)', () => {
  it('fires at the interval and again after each run', async () => {
    const t = setup()
    t.refresher.start()
    t.clock.advance(59_999)
    await flush()
    expect(t.log).toEqual([])
    t.clock.advance(1)
    await flush()
    expect(t.log).toEqual([1_060_000])
    t.clock.advance(60_000)
    await flush()
    t.clock.advance(60_000)
    await flush()
    expect(t.log).toEqual([1_060_000, 1_120_000, 1_180_000])
    expect(t.refresher.state.runs).toBe(3)
    expect(t.refresher.state.stale).toBe(false)
  })

  it('never goes below the floor, whatever the document asks', async () => {
    const t = setup({ everyMs: 1_000 })
    t.refresher.start()
    t.clock.advance(14_999)
    await flush()
    expect(t.log).toEqual([])
    t.clock.advance(1)
    await flush()
    expect(t.log.length).toBe(1)
    // the lowest the host allows is its own setting
    const low = setup({ everyMs: 1_000, floorMs: 2_000 })
    low.refresher.start()
    low.clock.advance(2_000)
    await flush()
    expect(low.log.length).toBe(1)
  })

  it('does not run while the page is hidden, and makes a refresh that came due when it shows again', async () => {
    const t = setup()
    t.refresher.start()
    t.clock.advance(30_000)
    t.refresher.setHidden(true)
    expect(t.refresher.state.paused).toBe(true)
    expect(t.clock.pending).toBe(0)
    t.clock.advance(600_000)
    await flush()
    expect(t.log).toEqual([])
    t.refresher.setHidden(false)
    await flush()
    expect(t.log).toEqual([1_630_000])
    expect(t.refresher.state.paused).toBe(false)
    // and the next is a full wait after that one
    t.clock.advance(59_999)
    await flush()
    expect(t.log.length).toBe(1)
    t.clock.advance(1)
    await flush()
    expect(t.log.length).toBe(2)
  })

  it('a page shown again before the refresh is due waits for the rest of the interval', async () => {
    const t = setup()
    t.refresher.start()
    t.clock.advance(10_000)
    t.refresher.setHidden(true)
    t.clock.advance(20_000)
    t.refresher.setHidden(false)
    await flush()
    expect(t.log).toEqual([])
    t.clock.advance(29_999)
    await flush()
    expect(t.log).toEqual([])
    t.clock.advance(1)
    await flush()
    expect(t.log.length).toBe(1)
  })

  it('a page that starts hidden asks for nothing until it is shown', async () => {
    const t = setup({ hidden: true })
    t.refresher.start()
    t.clock.advance(3_600_000)
    await flush()
    expect(t.log).toEqual([])
    expect(t.refresher.state.paused).toBe(true)
    t.refresher.setHidden(false)
    await flush()
    expect(t.log.length).toBe(1)
  })

  it('a failed refresh keeps the rows (the caller keeps them) and marks them stale until one succeeds', async () => {
    let failing = true
    const t = setup({ fail: () => failing })
    t.refresher.start()
    t.clock.advance(60_000)
    await flush()
    expect(t.refresher.state).toMatchObject({ stale: true, error: 'the source did not answer', runs: 1, running: false })
    t.clock.advance(60_000)
    await flush()
    expect(t.refresher.state).toMatchObject({ stale: true, runs: 2 })
    failing = false
    t.clock.advance(60_000)
    await flush()
    expect(t.refresher.state).toMatchObject({ stale: false, error: null, runs: 3 })
  })

  it('never starts a refresh while one is running, and stop ends everything', async () => {
    const t = setup()
    t.hold(true)
    t.refresher.start()
    t.clock.advance(60_000)
    await flush()
    expect(t.refresher.state.running).toBe(true)
    t.clock.advance(600_000)
    await flush()
    expect(t.log.length).toBe(1)
    t.hold(false)
    t.release()
    await flush()
    expect(t.refresher.state.running).toBe(false)
    t.refresher.stop()
    expect(t.clock.pending).toBe(0)
    t.clock.advance(600_000)
    await flush()
    expect(t.log.length).toBe(1)
  })

  it('hiding during a run does not schedule the next until the page shows', async () => {
    const t = setup()
    t.hold(true)
    t.refresher.start()
    t.clock.advance(60_000)
    await flush()
    t.refresher.setHidden(true)
    t.hold(false)
    t.release()
    await flush()
    expect(t.clock.pending).toBe(0)
    expect(t.refresher.state.paused).toBe(true)
    t.refresher.setHidden(false)
    await flush()
    expect(t.log.length).toBe(1)
    t.clock.advance(60_000)
    await flush()
    expect(t.log.length).toBe(2)
  })
})

describe('formatAsOf', () => {
  it('is hh:mm in the zone given, and empty for what is not a time', () => {
    expect(formatAsOf('2026-11-08T09:05:30Z', 'UTC')).toBe('09:05')
    expect(formatAsOf('2026-11-08T23:59:00Z', 'UTC')).toBe('23:59')
    expect(formatAsOf('2026-11-08T09:05:30Z', 'Asia/Tokyo')).toBe('18:05')
    expect(formatAsOf('not a time', 'UTC')).toBe('')
    expect(formatAsOf(null)).toBe('')
  })
})
