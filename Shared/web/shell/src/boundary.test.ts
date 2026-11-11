import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap(name => {
    const path = join(dir, name)
    return statSync(path).isDirectory() ? sources(path) : /\.(ts|tsx)$/.test(name) ? [path] : []
  })
}

/** The shell is what every product's console is made of, so it may not depend on any one product (plan 13, U02). */
describe('@dbdatakit/shell', () => {
  it('imports nothing from a product', () => {
    const offending = sources(join(import.meta.dirname)).flatMap(file =>
      [...readFileSync(file, 'utf8').matchAll(/from\s+['"]([^'"]+)['"]/g)]
        .map(m => m[1])
        .filter(spec => /^@dbdatakit\/(?!shell)/.test(spec) || /(^|\/)(sync|proxy|build)(\/|$)/i.test(spec))
        .map(spec => `${file}: ${spec}`))
    expect(offending).toEqual([])
  })

  it('has no relative import that leaves its own source folder', () => {
    const root = join(import.meta.dirname)
    const escaping = sources(root).flatMap(file => {
      const dir = file.slice(0, file.lastIndexOf('/'))
      return [...readFileSync(file, 'utf8').matchAll(/from\s+['"](\.[^'"]*)['"]/g)]
        .map(m => join(dir, m[1]!))
        .filter(resolved => !resolved.startsWith(root))
        .map(resolved => `${file}: ${resolved}`)
    })
    expect(escaping).toEqual([])
  })
})
