/**
 * The closed vocabulary of icons an attribute's value may show as (plan 32 DBR51): the core schema's `icon` enum, the rendered schema's `icon` and the client's glyph table are this list, and a test keeps them equal.
 * Nothing outside it is drawn: an app's own icon font is DBR37's.
 */
export const ICON_NAMES = [
  'circle', 'dot', 'check', 'cross', 'warning', 'info', 'clock', 'flag', 'star', 'pause', 'play', 'stop', 'lock', 'unlock', 'arrow-up', 'arrow-down', 'arrow-right', 'plus', 'minus', 'pin', 'bell', 'eye', 'user', 'tag',
] as const
export type IconName = (typeof ICON_NAMES)[number]
