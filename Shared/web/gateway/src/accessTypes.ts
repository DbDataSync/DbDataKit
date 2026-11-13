/** What the Gateway's access routes answer (plan 22a GP15): the rules of each model, the explanation, the coverage. The shapes are the management API's (`GatewayAccessManagement`). */

export type AccessState = 'fresh' | 'stale' | 'expired'

export interface AccessRuleColumn { dataset_column: string; rules_column: string }

export interface AccessRule {
  id: string
  effect: 'include' | 'exclude'
  table: string
  subject: string
  subject_kind: 'group' | 'person'
  columns: AccessRuleColumn[]
  max_age_seconds: number | null
}

export interface AccessRuleGroup { id: string; match: 'all' | 'any'; rules: AccessRule[] }

export interface AccessDatasetRules { dataset: string; match: 'all' | 'any'; groups: AccessRuleGroup[] }

export interface AccessColumnRule { subject: string; subject_kind: 'group' | 'person'; dataset: string; column: string; treatment: 'hide' | 'mask' | 'null'; mask: 'fixed' | 'hash' | null }

export interface AccessTable {
  table: string
  key: string
  state: AccessState
  age_seconds: number
  max_age_seconds: number
  counted_from: string
  last_changed: string | null
}

export interface AccessDefinition {
  reference: string
  name: string
  kind: 'semantic-model' | 'entity-model'
  version: number | null
  has_row_rules: boolean
  has_column_rules: boolean
  /** The rest is an administrator's: a viewer gets the names only. */
  max_age_seconds?: number
  row?: AccessDatasetRules[]
  columns?: AccessColumnRule[]
  tables?: AccessTable[]
  warnings?: string[]
  /** An entity model's row rules limit reads only, never writes (plan 22a QR6): the screen must say so. */
  limits_reads_only?: boolean
}

export interface AccessRulesAnswer { definitions: AccessDefinition[]; mask_key?: { exists: boolean } }

export interface AccessRulesRow { subject: string; values: Record<string, string | null> }

export interface AccessExplainedRule {
  id: string
  effect: 'include' | 'exclude'
  table: string
  subject: string
  subject_kind: 'group' | 'person'
  columns: AccessRuleColumn[]
  names: string[]
  rows: AccessRulesRow[]
  rows_truncated: boolean
  holds: boolean | null
  read_problem: string | null
}

export interface AccessExplainedGroup { id: string; match: 'all' | 'any'; included: boolean | null; rules: AccessExplainedRule[] }

export interface AccessVerdict { included: boolean; decided_by: string[]; text: string; row: Record<string, string | null> }

export interface AccessExplainedDataset {
  dataset: string
  match: 'all' | 'any'
  refusal: string | null
  summary: string
  groups: AccessExplainedGroup[]
  verdict: AccessVerdict | null
  missing_columns: string[]
}

export interface AccessExplanation {
  definition: string
  subject: string
  subject_kind: 'person' | 'group'
  names: string[]
  fingerprint: string
  datasets: AccessExplainedDataset[]
  columns: { dataset: string; column: string; treatment: string; rules: { subject: string; subject_kind: string; treatment: string; mask: string | null }[] }[]
  tables: AccessTable[]
  warnings: string[]
}

export interface AccessCoverageRule {
  dataset: string
  group: string
  rule: string
  effect: 'include' | 'exclude'
  table: string
  columns: AccessRuleColumn[]
  uncovered: number
  values: { values: Record<string, string | null>; rows: number }[]
  truncated: boolean
  problem: string | null
}

export interface AccessCoverage { definition: string; rules: AccessCoverageRule[] }

/** The explain form: who, which dataset, and the row asked about (column and value pairs; the text `null` is NULL). */
export interface ExplainForm {
  subject: string
  kind: 'person' | 'group'
  dataset: string
  row: { column: string; value: string }[]
}
