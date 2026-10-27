import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { conflictsOf } from './GitPanel'

describe('conflictsOf', () => {
  it('names the files a refused merge sent', () => {
    expect(conflictsOf(new ApiError(409, 'conflict', { conflicts: ['models/a.sql', 'dbdatabuild.yml'] }))).toEqual(['models/a.sql', 'dbdatabuild.yml'])
  })
  it('is empty for any other failure', () => {
    expect(conflictsOf(new ApiError(409, 'no remote', { error: 'x' }))).toEqual([])
    expect(conflictsOf(new Error('x'))).toEqual([])
  })
})
