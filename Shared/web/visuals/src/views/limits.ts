/**
 * The smallest box each widget type is drawn in. Kept apart from `document.ts` (which compiles the format's schemas when it is loaded) so that a chart that needs only the numbers (`widgets/density.ts`) does not load the validator:
 * an app's page runs under a Content-Security-Policy with no `unsafe-eval`, and compiling a schema is `new Function` (plan 22a GP34).
 */
export const MINIMUMS: Record<string, [number, number]> = { metric: [160, 88], text: [160, 56], control: [180, 56], chart: [280, 180], table: [320, 200], matrix: [360, 220], embed: [280, 180] }
