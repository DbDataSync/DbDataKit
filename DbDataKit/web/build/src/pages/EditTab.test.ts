import { describe, expect, it } from 'vitest'
import { groupByFolder, markersFor } from './EditTab'
import type { Diagnostic } from '../types'

const diagnostic = (file: string, severity: Diagnostic['severity'], line = 0): Diagnostic => ({ code: 'DDB-1', severity, title: 't', location: { file, line, column: 0 }, found: 'found', supported: '', fix: '' })

describe('the editor tab', () => {
  it('groups files by folder, the root first', () => {
    const files = [{ path: 'models/a.sql', size: 1, hash: 'a' }, { path: 'dbdatabuild.yml', size: 1, hash: 'b' }, { path: 'models/staging/b.sql', size: 1, hash: 'c' }]
    expect(groupByFolder(files).map((g) => [g.folder, g.files.length])).toEqual([['', 1], ['models', 1], ['models/staging', 1]])
  })

  it('marks only the open file, and not notes', () => {
    const markers = markersFor('models/a.sql', [diagnostic('models/a.sql', 'error', 3), diagnostic('models/a.sql', 'note'), diagnostic('models/b.sql', 'error'), diagnostic('models/a.sql', 'warning')])
    expect(markers.map((m) => [m.line, m.severity])).toEqual([[3, 'error'], [0, 'warning']])
    expect(markers[0]!.message).toBe('DDB-1: found')
  })
})
