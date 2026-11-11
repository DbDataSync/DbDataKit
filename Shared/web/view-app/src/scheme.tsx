import { applyShellTheme, type Scheme, type ShellTheme } from '@dbdatakit/store-ui'
import { useTheme } from '@dbdatakit/visuals'
import { createContext, useContext, useEffect, useState } from 'react'
import type { Host } from './data'

/** The scheme a shell fixed with its theme message; null where it said none (the system's is drawn). */
export const SchemeContext = createContext<Scheme | null>(null)

/** The scheme the charts draw in: the shell's when it fixed one, else the person's system setting. */
export function useScheme(): Scheme {
  const fixed = useContext(SchemeContext)
  const system = useTheme('auto')
  return fixed ?? system
}

/**
 * Follows the shell's `theme` messages (plan 26 AP28): the tokens of the scheme in force become custom properties on the document, so the shared controls draw in the portal's colours, and the scheme is returned for the charts.
 * Where the host has no theme (an instance outside a portal) nothing is set and null is returned.
 */
export function useShellTheme(host: Partial<Pick<Host, 'onTheme'>>, root: HTMLElement | null = typeof document === 'undefined' ? null : document.documentElement): Scheme | null {
  const [theme, setTheme] = useState<ShellTheme | null>(null)
  const system = useTheme('auto')
  useEffect(() => host.onTheme?.(setTheme), [host])
  const scheme = theme?.scheme ?? system
  useEffect(() => (theme && root ? applyShellTheme(root, theme, scheme) : undefined), [theme, scheme, root])
  return theme?.scheme ?? null
}
