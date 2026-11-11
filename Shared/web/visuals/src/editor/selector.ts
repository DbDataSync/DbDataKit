import type { Medium, SelectableLayout } from '../views/selectLayout'

/** The selector of a layout as the form holds it: text for each number (so a half-typed one is not lost), the orientation and the media. */
export interface SelectorFields {
  widthMin: string
  widthMax: string
  heightMin: string
  heightMax: string
  aspectMin: string
  aspectMax: string
  orientation: '' | 'portrait' | 'landscape'
  media: Medium[]
}

export const MEDIA: Medium[] = ['screen', 'print', 'export']

type When = NonNullable<SelectableLayout['when']>

export function fieldsFromWhen(when: When | undefined): SelectorFields {
  const media = when?.medium === undefined ? [] : Array.isArray(when.medium) ? when.medium : [when.medium]
  const s = (n: number | undefined) => (n === undefined ? '' : String(n))
  return {
    widthMin: s(when?.width?.min), widthMax: s(when?.width?.max), heightMin: s(when?.height?.min), heightMax: s(when?.height?.max), aspectMin: s(when?.aspect?.min), aspectMax: s(when?.aspect?.max),
    orientation: when?.orientation ?? '', media,
  }
}

const num = (text: string): number | undefined => {
  if (text.trim() === '') return undefined
  const n = Number(text)
  return Number.isFinite(n) ? n : undefined
}

/** The `when` the fields make, with every empty condition left out; `undefined` when there is none (the layout then has no selector). A medium list of one is written as the one name. */
export function whenFromFields(f: SelectorFields): When | undefined {
  const when: Record<string, unknown> = {}
  const range = (min: string, max: string) => {
    const r: { min?: number; max?: number } = {}
    const lo = num(min), hi = num(max)
    if (lo !== undefined) r.min = lo
    if (hi !== undefined) r.max = hi
    return Object.keys(r).length > 0 ? r : undefined
  }
  const width = range(f.widthMin, f.widthMax)
  const height = range(f.heightMin, f.heightMax)
  const aspect = range(f.aspectMin, f.aspectMax)
  if (width) when.width = width
  if (height) when.height = height
  if (f.orientation) when.orientation = f.orientation
  if (aspect) when.aspect = aspect
  if (f.media.length > 0) when.medium = f.media.length === 1 ? f.media[0] : MEDIA.filter((m) => f.media.includes(m))
  return Object.keys(when).length > 0 ? (when as When) : undefined
}

/** A number field's text that is not a number (`12x`): the form says so beside the field instead of dropping the condition silently. */
export function badNumbers(f: SelectorFields): string[] {
  return (['widthMin', 'widthMax', 'heightMin', 'heightMax', 'aspectMin', 'aspectMax'] as const).filter((k) => f[k].trim() !== '' && num(f[k]) === undefined)
}
