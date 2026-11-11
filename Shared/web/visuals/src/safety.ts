import { expressionInterpreter } from 'vega-interpreter'

/**
 * What stands between a chart spec and the network or `eval`, besides the validator.
 *
 * - `refusingLoader` is the loader every Vega view is given. Vega asks a loader for any URL a spec holds (data, images, a `$schema` is not one); the validator already
 *   refuses a spec that names one, and a loader that refuses everything makes that true even if a spec got past it.
 * - `expressionInterpreter` (`ast: true`) runs the expressions Vega-Lite itself writes into the compiled spec (scales, selections, tooltips) by interpreting their
 *   syntax tree, so the page needs no `unsafe-eval` and no string is ever compiled to code.
 */
export const refusingLoader = {
  baseURL: '',
  options: {},
  sanitize: (): Promise<never> => Promise.reject(new Error('A view cannot load anything.')),
  load: (): Promise<never> => Promise.reject(new Error('A view cannot load anything.')),
  fileAccess: false,
  file: (): Promise<never> => Promise.reject(new Error('A view cannot load anything.')),
  http: (): Promise<never> => Promise.reject(new Error('A view cannot load anything.')),
}

/** The Vega options that apply the two above. */
export const safeVegaOptions = { ast: true as const, expr: expressionInterpreter }
