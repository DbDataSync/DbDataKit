/** Icons for the admin screens — stroke-based, drawn inline so they scale and take `currentColor`. */

const stroke = {
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.4,
} as const

/** A plain checkmark — no circle, so it reads as "confirmed" beside text rather than as a status dot
 * (the rail/badge convention elsewhere already owns that shape). */
export function CheckIcon({ size = 12 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke} strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 8.5 6.2 11.7 13 4.5" />
    </svg>
  )
}

/** More detail available on hover — a circled question mark, for a short label with a longer
 * explanation behind it (the admin config table's descriptions, so far). */
export function HelpIcon({ size = 12 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 16 16" {...stroke} strokeLinecap="round" strokeLinejoin="round">
      <circle cx="8" cy="8" r="6.5" />
      <path d="M6.2 6.3a1.8 1.8 0 1 1 2.6 1.6c-.7.4-1 .8-1 1.6" />
      <circle cx="8" cy="11.5" r="0.2" fill="currentColor" stroke="none" />
    </svg>
  )
}
