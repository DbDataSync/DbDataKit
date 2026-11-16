import { describe, expect, it } from 'vitest'
import { removedProducts } from './ServerModule'

describe('a server whose product was deleted (plan 33 SC23)', () => {
  it('says where the Build and Store servers went instead of showing their screens', () => {
    expect(removedProducts.dbdatabuild).toContain('Workspace')
    expect(removedProducts.dbdatabuild).toContain('Runner')
    expect(removedProducts.dbdatastore).toContain('Workspace')
    expect(removedProducts.dbdatagateway).toBeUndefined()
  })
})
