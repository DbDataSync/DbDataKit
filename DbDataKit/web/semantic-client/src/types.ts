/** The wire shapes of a semantic query (docs/semantic/query.md), as a caller sees them. Nothing here depends on a host or a framework. */

/** Filter operators are the core grammar's (docs/semantic/core.md); the client passes them through and the server checks them. */
export interface SemanticFilter {
  attribute: string
  operator: string
  values?: unknown[]
}

/** What a person (or an app) asks for, in catalog terms: never a table, a join or an engine. `limit` is required (1 to 10,000). */
export interface SemanticQuery {
  measures: string[]
  attributes?: string[]
  filters?: SemanticFilter[]
  /** `[member or alias, direction]`, applied in sequence. */
  order?: [string, 'asc' | 'desc'][]
  limit: number
  segments?: string[]
  /** An IANA zone in which dates are read; the catalog's default when left out. */
  timezone?: string
}

export interface SemanticColumn {
  name: string
  /** `integer`, `number`, `boolean`, `date`, `datetime` or `string`, as the engine reports it. */
  type: string
}

export type SemanticValue = string | number | boolean | null

export interface SemanticResult {
  /** The engine's status (`ok` when the rows are complete). */
  status: string
  columns: SemanticColumn[]
  rows: Record<string, SemanticValue>[]
  warnings: string[]
  /** What the request hash of the engine says it ran; the same request gives the same hash. */
  requestHash: string | null
}

export interface ModelSummary {
  name: string
  title: string | null
  version: string | null
}

export interface CatalogMember {
  name: string
  title: string | null
  description: string | null
  type: string | null
}

/** What a model offers a request: its measures and attributes (dimensions' levels). */
export interface ModelCatalog {
  model: string
  measures: CatalogMember[]
  attributes: CatalogMember[]
}

export type SemanticErrorCode = 'not_found' | 'denied' | 'invalid' | 'clarify' | 'unsupported' | 'unavailable' | 'bad_response'

/** A refusal or a failure, with the status and the server's own words. `clarify` and `unsupported` are the engine's answers to a request it will not run (the reason is the message). */
export class SemanticError extends Error {
  readonly code: SemanticErrorCode
  readonly status: number | null
  readonly details: unknown
  constructor(message: string, code: SemanticErrorCode, status: number | null = null, details: unknown = null) {
    super(message)
    this.name = 'SemanticError'
    this.code = code
    this.status = status
    this.details = details
  }
}
