import { useSyncExternalStore } from 'react'

/** `auto` follows the person's operating system setting (`prefers-color-scheme`); the other two are fixed. */
export type ThemeChoice = 'light' | 'dark' | 'auto'
export type Theme = 'light' | 'dark'

interface Palette {
  text: string
  muted: string
  grid: string
  axis: string
  /** Categorical colours, chosen to stay apart for the common colour-vision differences (the Okabe-Ito set; lifted on the dark ground). */
  categories: string[]
}

const PALETTES: Record<Theme, Palette> = {
  light: { text: '#1f2328', muted: '#59636e', grid: '#d8dee4', axis: '#8c959f', categories: ['#0072B2', '#E69F00', '#009E73', '#D55E00', '#CC79A7', '#56B4E9', '#8c6d00', '#6b7280'] },
  dark: { text: '#e6edf3', muted: '#9da7b3', grid: '#30363d', axis: '#6e7681', categories: ['#56B4E9', '#F0B429', '#2FC79A', '#FF7F50', '#E08BC0', '#7CC6F5', '#D4B106', '#9AA4B2'] },
}

/**
 * The Vega-Lite configuration of a theme. A chart's colours cannot come from CSS variables (Vega writes them as SVG attributes), so each theme names them. This is
 * the renderer's own configuration, never part of a view document: a spec cannot set a `config`, which the subset leaves out.
 */
export function chartConfig(theme: Theme): Record<string, unknown> {
  const p = PALETTES[theme]
  const label = { labelColor: p.muted, titleColor: p.text, labelFont: 'system-ui, sans-serif', titleFont: 'system-ui, sans-serif' }
  return {
    background: null,
    view: { stroke: null, continuousWidth: 480, continuousHeight: 280 },
    title: { color: p.text, font: 'system-ui, sans-serif', fontSize: 14, fontWeight: 600, anchor: 'start' },
    axis: { ...label, domainColor: p.axis, gridColor: p.grid, tickColor: p.axis },
    legend: { ...label },
    range: { category: p.categories },
    mark: { color: p.categories[0] },
    text: { color: p.text },
    rule: { color: p.axis },
  }
}

const query = '(prefers-color-scheme: dark)'
const subscribe = (notify: () => void) => {
  if (typeof matchMedia !== 'function') return () => {}
  const media = matchMedia(query)
  media.addEventListener('change', notify)
  return () => media.removeEventListener('change', notify)
}
const systemTheme = (): Theme => (typeof matchMedia === 'function' && matchMedia(query).matches ? 'dark' : 'light')

/** The theme to draw with now: the fixed choice, or the system's (and it follows a change while the page is open). Light where there is no system to ask. */
export function useTheme(choice: ThemeChoice): Theme {
  const system = useSyncExternalStore(subscribe, systemTheme, () => 'light' as Theme)
  return choice === 'auto' ? system : choice
}
