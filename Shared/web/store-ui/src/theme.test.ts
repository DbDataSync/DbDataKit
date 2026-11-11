import { describe, expect, it } from 'vitest'
import { applyShellTheme, parseShellTheme, themeProperties } from './theme'

const message = { dbdata: 'app/1', type: 'theme', tokens: { light: { 'sui-accent': '#0969da', 'sui-text': '#111' }, dark: { 'sui-accent': '#58a6ff' } } }

describe('the shell theme message (AP28)', () => {
  it('reads light and dark tokens, and an optional scheme', () => {
    expect(parseShellTheme(message)).toEqual({ light: message.tokens.light, dark: message.tokens.dark, scheme: null })
    expect(parseShellTheme({ ...message, scheme: 'dark' })?.scheme).toBe('dark')
    expect(parseShellTheme({ ...message, scheme: 'sepia' })?.scheme).toBeNull()
  })

  it('is no theme without tokens, and drops a name or value that is not a token', () => {
    expect(parseShellTheme({ type: 'theme' })).toBeNull()
    expect(parseShellTheme({ tokens: { light: {}, dark: {} } })).toBeNull()
    const t = parseShellTheme({ tokens: { light: { 'sui-ok': 'red', 'Bad Name': 'red', 'sui-x': 'red; } body { display:none', 'sui-y': 'url(https://x)', 'sui-z': 3 }, dark: {} } })
    expect(t?.light).toEqual({ 'sui-ok': 'red' })
  })

  it('a dark scheme darkens the controls: dark tokens over the light ones', () => {
    const t = parseShellTheme(message)!
    expect(themeProperties(t, 'light')).toEqual({ '--sui-accent': '#0969da', '--sui-text': '#111' })
    expect(themeProperties(t, 'dark')).toEqual({ '--sui-accent': '#58a6ff', '--sui-text': '#111' })
  })

  it('sets the properties on an element and removes them again', () => {
    const style = new Map<string, string>()
    const root = { style: { setProperty: (n: string, v: string) => { style.set(n, v) }, removeProperty: (n: string) => { const v = style.get(n) ?? ''; style.delete(n); return v } } }
    const undo = applyShellTheme(root, parseShellTheme(message)!, 'dark')
    expect(style.get('--sui-accent')).toBe('#58a6ff')
    undo()
    expect(style.size).toBe(0)
  })
})
