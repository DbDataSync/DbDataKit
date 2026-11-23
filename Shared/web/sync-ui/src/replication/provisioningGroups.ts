import type { ProvisioningPlan, ProvisioningSide } from '@dbdatakit/workspace'

/** Which end of a mapping a step changes. */
export type Side = 'source' | 'target'

/** One statement of a replication's plan, once however many mappings need it, with the mappings that do. */
export interface GroupStep {
  title: string
  commandText: string
  rationale: string | null
  /** `database` or `table`. */
  scope: string
  mappings: string[]
}

/** A mapping's side that would change, which **Apply** runs as the task `provision` does: that mapping and that side's action. */
export interface ApplyTarget { mapping: string; action: string }

/** The steps of one connection on one side, in the order the mappings first need them. */
export interface ProvisioningGroup {
  connection: string
  side: Side
  steps: GroupStep[]
  warnings: string[]
  /** The mappings whose side here would change, each with the action that applies it. */
  apply: ApplyTarget[]
}

/** A mapping, or one side of it, the plan could not say anything about, and why. */
export interface ExcludedMapping { mapping: string; side: Side | null; reason: string }

export interface GroupedPlan { groups: ProvisioningGroup[]; excluded: ExcludedMapping[] }

/**
 * A replication's plan as the old Provisioning tab laid it out: grouped by connection and side, each statement once with the mappings that need
 * it, the sides that cannot be planned listed apart with why. The plan itself is per mapping (the Workspace's `provisioning-plan`, made offline from
 * the catalog cache); a statement two mappings need (a database setting, a schema) is the same text in both and is shown once.
 */
export function groupPlan(plan: ProvisioningPlan): GroupedPlan {
  const groups = new Map<string, ProvisioningGroup>()
  const excluded: ExcludedMapping[] = []
  for (const m of plan.mappings) {
    if (m.excluded) { excluded.push({ mapping: m.mapping, side: null, reason: m.excluded }); continue }
    const sides: [Side, ProvisioningSide | null, string | undefined][] = [['source', m.source_plan, m.source?.connection], ['target', m.target_plan, m.target?.connection]]
    for (const [side, p, connection] of sides) {
      if (!p || !connection) continue
      if (p.state === 'unsupported' || p.state === 'unknown') {
        excluded.push({ mapping: m.mapping, side, reason: p.warnings.join(' ') || (p.state === 'unknown' ? 'The catalog cache cannot say what this side needs.' : 'This side cannot be planned.') })
        continue
      }
      if (p.state !== 'missing' || p.steps.length === 0) continue
      const key = `${side}\u0000${connection}`
      const group = groups.get(key) ?? { connection, side, steps: [], warnings: [], apply: [] }
      groups.set(key, group)
      for (const s of p.steps) {
        const same = group.steps.find((g) => g.commandText === s.command_text && g.scope === s.scope)
        if (same) { if (!same.mappings.includes(m.mapping)) same.mappings.push(m.mapping) }
        else group.steps.push({ title: s.title, commandText: s.command_text, rationale: s.rationale, scope: s.scope, mappings: [m.mapping] })
      }
      for (const w of p.warnings) if (!group.warnings.includes(w)) group.warnings.push(w)
      group.apply.push({ mapping: m.mapping, action: p.action })
    }
  }
  const order = (g: ProvisioningGroup) => `${g.side === 'source' ? 0 : 1}${g.connection}`
  return { groups: [...groups.values()].sort((a, b) => order(a).localeCompare(order(b))), excluded }
}

/** The statements of one group as a script a DBA can run, with a header naming the replication, the connection and the side. */
export function scriptFor(replication: string, group: ProvisioningGroup, generated: string): string {
  const header = [
    '-- DbDataKit Sync provisioning script',
    `-- Replication: ${replication}`,
    `-- Connection: ${group.connection} (${group.side})`,
    `-- Generated: ${generated}`,
    '',
    '',
  ].join('\n')
  return header + group.steps.map((s) => s.commandText).join('\n')
}
