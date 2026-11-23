import type { ParameterDescriptor } from '../types'

const SINGLE = { min: 1, max: 1 }

/** How many values a parameter takes, one when it does not say. */
export function occurrences(parameter: ParameterDescriptor) {
  return parameter.cardinality ?? SINGLE
}

export function isVararg(parameter: ParameterDescriptor) {
  const { min, max } = occurrences(parameter)
  return min !== 1 || max !== 1
}

/** The values of a vararg, which live under `<name>.<key>` in the same flat dictionary: the options are a string dictionary in the file. */
export function varargEntries(parameter: ParameterDescriptor, values: Record<string, string>) {
  const prefix = `${parameter.name}.`
  return Object.fromEntries(
    Object.entries(values).filter(([k]) => k.startsWith(prefix)).map(([k, v]) => [k.slice(prefix.length), v]),
  )
}

/** This parameter's values replaced whole, everything else left alone; rebuilt rather than merged, so a removed key goes. */
export function withVararg(
  parameter: ParameterDescriptor, values: Record<string, string>, next: Record<string, string>,
): Record<string, string> {
  const prefix = `${parameter.name}.`
  return {
    ...Object.fromEntries(Object.entries(values).filter(([k]) => !k.startsWith(prefix))),
    ...Object.fromEntries(Object.entries(next).map(([k, v]) => [`${prefix}${k}`, v])),
  }
}
