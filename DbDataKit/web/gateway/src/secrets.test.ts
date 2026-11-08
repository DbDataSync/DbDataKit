import { describe, expect, it } from 'vitest'
import { afterCall, checkSecret, passwordLine, sorted } from './secrets'

const have = [{ name: 'erp-login', reference: 'secret:dbdata:gateway:managed-console-1:erp-login' }]

describe('the secret form', () => {
  it('asks for the name, then the value, saying so', () => {
    expect(checkSecret({ name: '', value: '' }, have).why).toBe('Name the secret.')
    expect(checkSecret({ name: 'warehouse', value: '' }, have)).toMatchObject({ ok: false, replaces: false, why: 'Type the value to keep.' })
  })

  it('is ready with a name and a value, and says whether it replaces one', () => {
    expect(checkSecret({ name: 'warehouse', value: 'x' }, have)).toEqual({ ok: true, replaces: false, why: null, nameProblem: null })
    expect(checkSecret({ name: ' erp-login ', value: 'x' }, have)).toMatchObject({ ok: true, replaces: true })
    expect(checkSecret({ name: 'erp-login', value: '' }, have).why).toContain('old one is never shown')
  })

  it('refuses a name the Gateway would refuse, in its words', () => {
    for (const bad of ['9lives', 'has space', '../x', 'a/b', 'x'.repeat(65)]) {
      const c = checkSecret({ name: bad, value: 'x' }, [])
      expect(c.ok, bad).toBe(false)
      expect(c.nameProblem, bad).toContain('A secret name is a letter')
    }
    for (const good of ['a', 'erp.login_2-b', 'x'.repeat(64)]) expect(checkSecret({ name: good, value: 'x' }, []).ok, good).toBe(true)
  })

  it('writes the line a data source file uses and keeps the names in order', () => {
    expect(passwordLine('secret:a:b')).toBe("password_secret: 'secret:a:b'")
    expect(sorted([{ name: 'b', reference: '' }, { name: 'a', reference: '' }]).map((s) => s.name)).toEqual(['a', 'b'])
  })

  it('keeps no value once a call is answered, whether it went through or was refused', () => {
    expect(afterCall({ name: 'erp', value: 'hunter2' }, true)).toEqual({ name: '', value: '' })
    expect(afterCall({ name: 'erp', value: 'hunter2' }, false)).toEqual({ name: 'erp', value: '' })
  })
})
