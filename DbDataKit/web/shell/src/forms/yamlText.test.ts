import { describe, expect, it } from 'vitest'
import { appendAt, keysAt, moveIn, parseYaml, removeAt, setAt, toYaml } from './yamlText'

const SAMPLE = `# the orders model
name: marts.fct_orders   # keep this
kind: full
columns:
  - {name: a, type: INT}   # the key
  - name: b
    type: VARCHAR(20)
`

describe('editing YAML text through a form', () => {
  it('changes one value and leaves comments, order and styles alone', () => {
    const next = setAt(SAMPLE, ['columns', 1, 'type'], 'VARCHAR(40)')
    expect(next).toBe(SAMPLE.replace('VARCHAR(20)', 'VARCHAR(40)'))
  })

  it('keeps the comment on a scalar that is replaced by another scalar', () => {
    expect(setAt(SAMPLE, ['name'], 'marts.fct_sales')).toContain('name: marts.fct_sales   # keep this')
    expect(setAt(SAMPLE, ['columns', 0, 'name'], 'z')).toContain('- {name: z, type: INT}   # the key')
  })

  it('adds a key at the end of its mapping and makes a missing parent', () => {
    expect(setAt(SAMPLE, ['grain'], ['a'])).toContain('grain:\n  - a\n')
    expect(parseYaml(setAt('name: x\n', ['defaults', 'tags'], ['finance'])).value).toEqual({ name: 'x', defaults: { tags: ['finance'] } })
  })

  it('removes a key or a list item', () => {
    expect(parseYaml(removeAt(SAMPLE, ['kind'])).value).not.toHaveProperty('kind')
    expect((parseYaml(removeAt(SAMPLE, ['columns', 0])).value as { columns: unknown[] }).columns).toHaveLength(1)
    expect(removeAt(SAMPLE, ['kind'])).toContain('# the orders model')
  })

  it('appends to a list, making it when it is missing, and moves items', () => {
    expect((parseYaml(appendAt(SAMPLE, ['columns'], { name: 'c', type: 'INT' })).value as { columns: { name: string }[] }).columns.map((c) => c.name)).toEqual(['a', 'b', 'c'])
    expect(parseYaml(appendAt('name: x\n', ['tags'], 'new')).value).toEqual({ name: 'x', tags: ['new'] })
    expect((parseYaml(moveIn(SAMPLE, ['columns'], 1, 0)).value as { columns: { name: string }[] }).columns.map((c) => c.name)).toEqual(['b', 'a'])
  })

  it('starts a document from nothing', () => {
    expect(parseYaml(setAt('', ['name'], 'x')).value).toEqual({ name: 'x' })
  })

  it('refuses to edit text that does not parse, and says so', () => {
    expect(() => setAt('a: [b\n', ['a'], 1)).toThrow(/not valid YAML/)
    expect(parseYaml('a: [b\n').errors[0]).toMatch(/^line \d+/)
  })

  it('lists keys in file order and writes values back as YAML', () => {
    expect(keysAt(SAMPLE, [])).toEqual(['name', 'kind', 'columns'])
    expect(keysAt(SAMPLE, ['columns', 0])).toEqual(['name', 'type'])
    expect(toYaml({ a: [1, 2] })).toBe('a:\n  - 1\n  - 2')
  })
})
