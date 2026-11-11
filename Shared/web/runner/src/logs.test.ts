import { describe, expect, it } from 'vitest'
import { runnerApi } from './api'
import { appendLines, isProblem, isWarning, lastId, lineText, MAX_LINES, shown, SHOWN_LENGTH, type RunLogLine } from './logs'

const line = (id: number, over: Partial<RunLogLine> = {}): RunLogLine => ({ id, at: '2026-11-11T10:20:30.456Z', level: 'info', item: null, message: `line ${id}`, ...over })

describe('a run\'s output', () => {
  it('adds the lines of a page in order and ignores the ones already shown', () => {
    const first = appendLines([], [line(1), line(2)])
    expect(appendLines(first, [line(2), line(3)]).map((l) => l.id)).toEqual([1, 2, 3])
    expect(appendLines(first, []).map((l) => l.id)).toEqual([1, 2])
  })

  it('keeps the newest lines when a run writes more than the screen holds', () => {
    const many = Array.from({ length: MAX_LINES + 5 }, (_, i) => line(i + 1))
    const kept = appendLines([], many)
    expect(kept).toHaveLength(MAX_LINES)
    expect(kept[0]!.id).toBe(6)
    expect(lastId(kept)).toBe(MAX_LINES + 5)
  })

  it('reads the next page after the last line seen, and from the start when there is none', () => {
    expect(lastId([])).toBeUndefined()
    expect(lastId([line(4), line(9)])).toBe(9)
  })

  it('tells an error from a warning from the rest, whatever case the runner recorded', () => {
    expect([isProblem('Error'), isProblem('critical'), isProblem('info'), isWarning('Warning'), isWarning('warn'), isWarning('error')]).toEqual([true, true, false, true, true, false])
  })

  it('writes a line as time, level, item and message for copying, and cuts only what it shows', () => {
    expect(lineText(line(1, { level: 'warning', item: 'orders#1', message: 'slow' }))).toBe('10:20:30.456 WARNING [orders#1] slow')
    const long = 'x'.repeat(SHOWN_LENGTH + 10)
    expect(shown(long)).toBe(`${'x'.repeat(SHOWN_LENGTH)}… (10 more characters)`)
    expect(shown('short')).toBe('short')
    expect(lineText(line(2, { message: long })).endsWith(long)).toBe(true)
  })

  it('asks the runner for the lines after an id, a page at a time, through the relay of its address', async () => {
    const asked: string[] = []
    const real = globalThis.fetch
    globalThis.fetch = (async (url: string) => { asked.push(String(url)); return new Response(JSON.stringify({ run: 'r1', lines: [line(5)] }), { status: 200, headers: { 'content-type': 'application/json' } }) }) as typeof fetch
    try {
      expect((await runnerApi.runLog('r 1')).lines).toHaveLength(1)
      await runnerApi.runLog('r1', 41, 50)
      expect(asked.map((u) => u.replace(/^.*\/api\//, '/api/'))).toEqual(['/api/v1/runs/r%201/logs?limit=200', '/api/v1/runs/r1/logs?limit=50&after=41'])
    } finally { globalThis.fetch = real }
  })
})
