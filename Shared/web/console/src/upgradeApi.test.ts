import { describe, expect, it } from 'vitest'
import { newestLine } from './upgradeApi'

describe('upgradeApi helpers', () => {
  it('says what the feed offers a server', () => {
    const row = { id: 'a', name: 'a', product: 'x', version: '1', generation: 1, newest: '2', newerAvailable: true, newestGeneration: null, notesUrl: null, outsideAfterRelease: null }
    expect(newestLine(row)).toBe('Newer available: 2')
    expect(newestLine({ ...row, newerAvailable: false })).toBe('Up to date')
    expect(newestLine({ ...row, newest: null })).toBe('No release in the feed')
  })
})
