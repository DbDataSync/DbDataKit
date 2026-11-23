import type { ParameterDescriptor } from '../types'

/** The writer kind that versions rows rather than replacing them. */
export const SCD2_WRITER = 'Scd2'

/** Its one declared setting: which columns identify a row across its versions. */
export const NATURAL_KEY = 'naturalKey'

/** Whether a natural key is a question this writer kind has: it is a property of what SCD Type 2 means, so it is matched on the kind. */
export const versionsRows = (writerKind: string | undefined) => writerKind === SCD2_WRITER

/** The declared settings without `naturalKey`, for a form that shows the natural key another way (derived from the mapping's key). */
export const withoutNaturalKey = <T extends ParameterDescriptor>(parameters: T[]) =>
  parameters.filter((p) => p.name !== NATURAL_KEY)
