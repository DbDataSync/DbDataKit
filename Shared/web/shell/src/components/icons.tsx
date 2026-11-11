/** The icons the shell itself draws — stroke-based, inline so they scale and take `currentColor`. A product's own icons (its mark, its navigation) stay with the product. */

const stroke = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.4,
} as const

/** Notifications — a bell, drawn open-bottomed so the badge can sit over its shoulder. */
export function BellIcon({ size = 15 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke} strokeLinecap="round" strokeLinejoin="round">
      <path d="M3.5 11.5V7a4.5 4.5 0 0 1 9 0v4.5" />
      <path d="M2.5 11.5h11" />
      <path d="M6.5 13.5a1.6 1.6 0 0 0 3 0" />
    </svg>
  )
}
