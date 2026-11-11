import { describe, expect, it } from 'vitest'
import type { RunnerInstance } from './api'
import { adminLabels, labelProblem, labelsBlock, labelsChanged, labelsRequest, missingLabels, normalizeLabel, reportedLabels, withLabel, withoutLabel } from './labels'

const instance = (labels: [string, string][] = [['os:linux', 'reported'], ['gpu', 'admin']], live?: boolean): RunnerInstance => ({
  id: 'i-1', host: 'h', version: '1', started: '', heartbeat: '', this: false, live, labels: labels.map(([label, source]) => ({ label, source })), capabilities: {}, running: 0,
})

describe('labels as the runner writes them', () => {
  it('trims and removes the spaces around a colon', () => {
    expect(normalizeLabel('  site : oslo ')).toBe('site:oslo')
    expect(normalizeLabel('gpu')).toBe('gpu')
    expect(normalizeLabel('   ')).toBe('')
  })
  it('splits the instance\'s labels by who set them', () => {
    expect(adminLabels(instance())).toEqual(['gpu'])
    expect(reportedLabels(instance())).toEqual(['os:linux'])
  })
})

describe('adding a label', () => {
  const reported = ['os:linux']
  it('explains what cannot be added and says nothing for an empty box', () => {
    expect(labelProblem('', [], reported)).toBeNull()
    expect(labelProblem('has space', [], reported)).toContain('is not a label')
    expect(labelProblem('-x', [], reported)).toContain('is not a label')
    expect(labelProblem('x'.repeat(65), [], reported)).toContain('is not a label')
    expect(labelProblem(' os : linux ', [], reported)).toContain('reported by the runner itself')
    expect(labelProblem('gpu', ['gpu'], reported)).toContain('in this list already')
    expect(labelProblem('l32', Array.from({ length: 32 }, (_, i) => `l${i}`), reported)).toContain('at most 32')
    expect(labelProblem('site:oslo', ['gpu'], reported)).toBeNull()
  })
  it('keeps the draft sorted, and leaves it alone for text it cannot add', () => {
    expect(withLabel(['gpu'], ' site : oslo ', [])).toEqual(['gpu', 'site:oslo'])
    expect(withLabel(['site:oslo'], 'gpu', [])).toEqual(['gpu', 'site:oslo'])
    const draft = ['gpu']
    expect(withLabel(draft, 'gpu', [])).toBe(draft)
    expect(withLabel(draft, '', [])).toBe(draft)
    expect(withoutLabel(['gpu', 'x'], 'gpu')).toEqual(['x'])
  })
})

describe('saving', () => {
  it('knows when the draft is what the instance has', () => {
    expect(labelsChanged(instance(), ['gpu'])).toBe(false)
    expect(labelsChanged(instance(), [])).toBe(true)
    expect(labelsChanged(instance(), ['gpu', 'x'])).toBe(true)
  })
  it('says why Save is off: the role, a gone runner, a bad or unadded text, no change; then allows it', () => {
    const i = instance()
    expect(labelsBlock(false, i, ['gpu'], '', ['os:linux'])).toContain('admin role')
    expect(labelsBlock(true, instance(undefined, false), ['x'], '', [])).toContain('gone')
    expect(labelsBlock(true, i, ['gpu'], 'bad label', ['os:linux'])).toContain('is not a label')
    expect(labelsBlock(true, i, ['gpu'], 'site:oslo', ['os:linux'])).toContain('Add the label you typed')
    expect(labelsBlock(true, i, ['gpu'], '', ['os:linux'])).toBe('Add or remove a label first.')
    expect(labelsBlock(true, i, ['gpu', 'site:oslo'], '', ['os:linux'])).toBeNull()
    expect(labelsBlock(true, i, [], '', ['os:linux'])).toBeNull()   // removing the last one is a change
  })
  it('sends the whole list, sorted', () => {
    expect(labelsRequest(['site:oslo', 'gpu'])).toEqual({ labels: ['gpu', 'site:oslo'] })
  })
})

describe('what a waiting task needs', () => {
  it('names the required labels no live runner has', () => {
    const live = instance([['gpu', 'admin'], ['os:linux', 'reported']])
    const gone = instance([['site:oslo', 'admin']], false)
    expect(missingLabels(['gpu', 'site:oslo', 'ssd'], [live, gone])).toEqual(['site:oslo', 'ssd'])
    expect(missingLabels([], [live])).toEqual([])
  })
})
