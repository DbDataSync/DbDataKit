import { useMemo } from 'react'
import { parseYaml } from '@dbdatakit/shell'

/**
 * The engines a sync project's connections may name, from the suite's engine registry: the descriptors `DbDataKit.Connections.Descriptor` embeds
 * (`engines/*.engine.yaml`), read into the web build as text so the screens and the registry never disagree. An engine's `sync_source` and
 * `sync_target` roles say what it may be offered as: `supported` and `unverified` are offered, `planned` is shown as planned and not offered, `no`
 * (or a role the descriptor leaves out) is not listed. A driver the draft carries itself (`drivers/<id>/driver.yaml` or `driver.csx`) is outside
 * the registry and shown as custom; what it can read or write is the driver's own, checked when the draft is checked.
 */
const registry = import.meta.glob('../../../src/DbDataKit.Connections.Descriptor/engines/*.engine.yaml', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

export type RoleStatus = 'supported' | 'unverified' | 'planned' | 'no'
export const SYNC_SOURCE = 'sync_source'
export const SYNC_TARGET = 'sync_target'
export type SyncRole = typeof SYNC_SOURCE | typeof SYNC_TARGET

export interface EngineRole { status: RoleStatus; note: string | null; offered: boolean }
export interface SyncEngine {
  id: string
  displayName: string
  aliases: string[]
  /** A driver the draft carries (`drivers/<id>/`), not an engine of the registry. */
  custom: boolean
  source: EngineRole
  target: EngineRole
}
export interface SyncEngineList {
  /** Every engine with a Sync role that is not `no`, and every custom driver, in id order. */
  all: SyncEngine[]
  /** What a source connection may name (offered), and what it may not yet (planned). */
  sources: SyncEngine[]
  targets: SyncEngine[]
  /** The engine of a connection's `engine:` value, by id or alias; undefined for a name neither the registry nor the draft's drivers know. */
  find: (name: string) => SyncEngine | undefined
}

const STATUSES: readonly RoleStatus[] = ['supported', 'unverified', 'planned', 'no']

function roleOf(roles: unknown, name: SyncRole): EngineRole {
  const r = (roles && typeof roles === 'object' ? (roles as Record<string, unknown>)[name] : undefined) as { status?: unknown; note?: unknown } | undefined
  const status = STATUSES.includes(r?.status as RoleStatus) ? (r!.status as RoleStatus) : 'no'
  return { status, note: typeof r?.note === 'string' ? r.note : null, offered: status === 'supported' || status === 'unverified' }
}

/** One descriptor's text as an engine, or null when it does not parse (the registry's own tests refuse such a file; the screens pass over it). */
export function engineOfDescriptor(text: string): SyncEngine | null {
  const doc = parseYaml(text).value as Record<string, unknown> | undefined
  if (!doc || typeof doc.id !== 'string') return null
  return {
    id: doc.id,
    displayName: typeof doc.display_name === 'string' ? doc.display_name : doc.id,
    aliases: Array.isArray(doc.aliases) ? doc.aliases.filter((a): a is string => typeof a === 'string') : [],
    custom: false,
    source: roleOf(doc.roles, SYNC_SOURCE),
    target: roleOf(doc.roles, SYNC_TARGET),
  }
}

const CUSTOM: EngineRole = { status: 'supported', note: 'a driver of this project: what it reads and writes is its own, checked when the draft is checked', offered: true }

/** The ids of the drivers a draft carries: `drivers/<id>/driver.yaml` (a descriptor) or `drivers/<id>/driver.csx` (a compiled driver). */
export function customDriversOf(paths: readonly string[]): string[] {
  const ids = new Set<string>()
  for (const p of paths) {
    const m = /^drivers\/([^/]+)\/driver\.(yaml|csx)$/.exec(p)
    if (m) ids.add(m[1])
  }
  return [...ids].sort()
}

/** The lists from the registry's descriptor texts and the draft's paths; the hook below gives it the built-in registry. */
export function syncEnginesFrom(descriptors: readonly string[], paths: readonly string[]): SyncEngineList {
  const known = descriptors.map(engineOfDescriptor).filter((e): e is SyncEngine => e !== null && (e.source.status !== 'no' || e.target.status !== 'no'))
  const taken = new Set(known.flatMap((e) => [e.id, ...e.aliases]))
  const custom: SyncEngine[] = customDriversOf(paths).filter((id) => !taken.has(id))
    .map((id) => ({ id, displayName: id, aliases: [], custom: true, source: CUSTOM, target: CUSTOM }))
  const all = [...known, ...custom].sort((a, b) => a.id.localeCompare(b.id))
  const byName = new Map<string, SyncEngine>()
  for (const e of all) for (const n of [e.id, ...e.aliases]) byName.set(n.toLowerCase(), e)
  return {
    all,
    sources: all.filter((e) => e.source.status !== 'no'),
    targets: all.filter((e) => e.target.status !== 'no'),
    find: (name) => byName.get(name.trim().toLowerCase()),
  }
}

/** The registry's descriptors as the web build holds them. */
export const registryDescriptors = (): string[] => Object.values(registry)

/** A role in words for a list or a title: `planned` and `unverified` carry their note. */
export function roleWords(role: EngineRole): string {
  if (role.status === 'supported') return role.note ?? 'supported'
  if (role.status === 'no') return 'not supported'
  return `${role.status}${role.note ? `: ${role.note}` : ''}`
}

/**
 * A sync project's engine lists, the one source the Files area's Sync forms and the Designer's screens both read: the registry's engines with
 * their Sync roles and the draft's own drivers as custom. `paths` are the draft's files.
 */
export function useSyncEngines(paths: readonly string[]): SyncEngineList {
  const key = customDriversOf(paths).join('\n')
  // the list changes only when the draft's drivers do, so it is keyed by their ids, not by the paths array
  return useMemo(() => syncEnginesFrom(registryDescriptors(), paths), [key])
}
