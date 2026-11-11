/**
 * The shell's `theme` message (plan 26 AP28): a portal's look reaches the app inside its frame as `{dbdata: 'app/1', type: 'theme', tokens: {light: {...}, dark: {...}}}`, each token a custom property such as `sui-accent`.
 * The controls draw with `--sui-*` variables, so applying the tokens is all it takes for them to look like the portal. `scheme` is optional and additive: a shell with a switch of its own says which scheme to draw in,
 * otherwise the system's is used. Nothing here touches a document; the caller applies the result.
 */
export type Scheme = 'light' | 'dark'

export interface ShellTheme { light: Record<string, string>; dark: Record<string, string>; scheme: Scheme | null }

const TOKEN = /^[a-z][a-z0-9-]{0,63}$/
// a value is a colour, a size or a font list; what could end a declaration, open a block or a comment, or fetch something is refused (the host's own loader limits values too, and an app is not trusted to have been given only that)
const UNSAFE = /[;{}<>\\]|\/\*|url\(|@import|expression\(/i

function tokens(raw: unknown): Record<string, string> {
  const out: Record<string, string> = {}
  if (!raw || typeof raw !== 'object') return out
  for (const [name, value] of Object.entries(raw as Record<string, unknown>)) {
    if (TOKEN.test(name) && typeof value === 'string' && value.length > 0 && value.length <= 200 && !UNSAFE.test(value)) out[name] = value
  }
  return out
}

/** The theme a message carries, or null when it carries none (a message without tokens is not a theme). */
export function parseShellTheme(message: unknown): ShellTheme | null {
  if (!message || typeof message !== 'object') return null
  const m = message as { tokens?: { light?: unknown; dark?: unknown }; scheme?: unknown }
  if (!m.tokens || typeof m.tokens !== 'object') return null
  const light = tokens(m.tokens.light)
  const dark = tokens(m.tokens.dark)
  if (Object.keys(light).length === 0 && Object.keys(dark).length === 0) return null
  return { light, dark, scheme: m.scheme === 'light' || m.scheme === 'dark' ? m.scheme : null }
}

/** The custom properties to set for a scheme: the light tokens, and over them the dark ones in a dark scheme (the host writes light on `:root` and dark under the browser's preference, the same order). */
export function themeProperties(theme: ShellTheme, scheme: Scheme): Record<string, string> {
  const merged = scheme === 'dark' ? { ...theme.light, ...theme.dark } : theme.light
  return Object.fromEntries(Object.entries(merged).map(([name, value]) => [`--${name}`, value]))
}

/** Sets the theme's properties on an element and returns a function that removes them again (a theme replaced by the next message, or by none). */
export function applyShellTheme(root: { style: { setProperty(name: string, value: string): void; removeProperty(name: string): string } }, theme: ShellTheme, scheme: Scheme): () => void {
  const props = themeProperties(theme, scheme)
  for (const [name, value] of Object.entries(props)) root.style.setProperty(name, value)
  return () => { for (const name of Object.keys(props)) root.style.removeProperty(name) }
}
