/** The Store's wire shapes (plan 19, Z08 to Z11), as the controls and a typed client see them. Nothing here depends on a host. */

export type AttributeKind =
  | 'boolean' | 'int8' | 'int16' | 'int32' | 'int64' | 'decimal' | 'float' | 'double' | 'string' | 'binary'
  | 'date' | 'time' | 'timestamp' | 'guid' | 'json' | 'xml' | 'interval' | 'list' | 'unmappable'

export interface AttributeSchema {
  name: string
  title: string | null
  type: string
  kind: AttributeKind
  length: number | null
  precision: number | null
  scale: number | null
  required: boolean
  unique: boolean
  default: string | null
  pattern: string | null
  min: number | null
  max: number | null
  allowed: string[] | null
  references: { model: string | null; entity: string; attribute: string | null } | null
}

export interface EntitySchema {
  name: string
  title: string | null
  key: string[]
  history: 'none' | 'audit' | 'effectivedated'
  role: string
  attributes: AttributeSchema[]
  rules: { kind: string; attribute: string | null; group_by: string[] }[]
}

export interface ModelSchema {
  name: string
  title: string | null
  approval: string
  entities: EntitySchema[]
}

export type RowValues = Record<string, unknown>

export interface StoredRow {
  values: RowValues
  version: number
  valid_from: string | null
  valid_to: string | null
}

export interface RowPage {
  rows: StoredRow[]
  limit: number
  offset: number
}

export interface Violation {
  rule: string
  entity: string
  attribute: string | null
  key: RowValues
  message: string
}

export type RowChange =
  | { action: 'insert'; entity: string; values: RowValues; effective_from?: string }
  | { action: 'update'; entity: string; key: RowValues; expected_version: number; values: RowValues; effective_from?: string }
  | { action: 'delete'; entity: string; key: RowValues; expected_version: number; effective_from?: string }

/** A failed call: its HTTP status, the server's sentence, and what it said was broken (422) or the row's current version (409). */
export class StoreError extends Error {
  readonly status: number
  readonly violations: Violation[]
  readonly currentVersion: number | null
  constructor(message: string, status: number, violations: Violation[] = [], currentVersion: number | null = null) {
    super(message)
    this.name = 'StoreError'
    this.status = status
    this.violations = violations
    this.currentVersion = currentVersion
  }
}
