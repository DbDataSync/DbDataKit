import type { FlowLayoutDoc } from '../views/model'

/**
 * The page of a flow layout (plan 32 section 4): its size, orientation and margins in millimetres, and the same in CSS pixels at 96 per inch. Pure; the screen view and the exporter read the same numbers,
 * so the page-break guides on screen are at the printable height the PDF uses.
 */
export const PAGE_SIZES_MM: Record<string, readonly [number, number]> = {
  A3: [297, 420],
  A4: [210, 297],
  A5: [148, 210],
  Letter: [215.9, 279.4],
  Legal: [215.9, 355.6],
}

/** The margins when a layout gives none (the plan's example: 20 mm above and below, 15 mm at the sides). */
export const DEFAULT_MARGINS_MM = { top: 20, right: 15, bottom: 20, left: 15 } as const

export const mmToPx = (mm: number): number => Math.round(((mm * 96) / 25.4) * 100) / 100

export interface Margins { top: number; right: number; bottom: number; left: number }

export interface PageGeometry {
  /** `A4` and so on, or `custom`. */
  size: string
  orientation: 'portrait' | 'landscape'
  widthMm: number
  heightMm: number
  margins: Margins
  contentWidthMm: number
  contentHeightMm: number
  widthPx: number
  heightPx: number
  marginsPx: Margins
  /** The printable area in CSS pixels: what a page holds. */
  contentWidthPx: number
  contentHeightPx: number
  /** The value of `@page { size: ... }`. */
  cssSize: string
}

type PageDoc = FlowLayoutDoc['page']

/** The geometry of a layout's `page` (A4 portrait with the default margins when it says nothing). A named size is portrait unless the orientation says landscape; a `{w, h}` size is taken as written, then turned if the orientation asks. */
export function pageGeometry(page: PageDoc | undefined): PageGeometry {
  const raw = (page ?? {}) as { size?: string | { w: number; h: number }; orientation?: 'portrait' | 'landscape'; margins?: Partial<Margins> }
  let size = 'A4'
  let [w, h]: [number, number] = [...PAGE_SIZES_MM.A4!]
  if (typeof raw.size === 'string' && PAGE_SIZES_MM[raw.size]) {
    size = raw.size
    ;[w, h] = [...PAGE_SIZES_MM[raw.size]!]
  } else if (raw.size && typeof raw.size === 'object') {
    size = 'custom'
    ;[w, h] = [raw.size.w, raw.size.h]
  }
  if (raw.orientation === 'landscape' && h > w) [w, h] = [h, w]
  else if (raw.orientation === 'portrait' && w > h) [w, h] = [h, w]
  const orientation = w > h ? 'landscape' : 'portrait'
  const margins: Margins = { ...DEFAULT_MARGINS_MM, ...raw.margins }
  const contentWidthMm = Math.max(1, w - margins.left - margins.right)
  const contentHeightMm = Math.max(1, h - margins.top - margins.bottom)
  return {
    size,
    orientation,
    widthMm: w,
    heightMm: h,
    margins,
    contentWidthMm,
    contentHeightMm,
    widthPx: mmToPx(w),
    heightPx: mmToPx(h),
    marginsPx: { top: mmToPx(margins.top), right: mmToPx(margins.right), bottom: mmToPx(margins.bottom), left: mmToPx(margins.left) },
    contentWidthPx: mmToPx(contentWidthMm),
    contentHeightPx: mmToPx(contentHeightMm),
    cssSize: size === 'custom' ? `${w}mm ${h}mm` : `${size} ${orientation}`,
  }
}
