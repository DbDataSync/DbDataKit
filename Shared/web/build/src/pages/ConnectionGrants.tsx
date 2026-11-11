import { useState } from 'react'
import { parseYaml, removeAt, setAt } from '@dbdatakit/shell'

/** Who may use a connection once it is published to a Gateway as a data source: home groups, or everyone (plan 22a GP03; `grants:` of a `connection/2` file). */
export interface Grants {
  groups: string[]
  everyone: boolean
}

export const noGrants: Grants = { groups: [], everyone: false }

/** A connection file of a project: `connections/<name>.yaml`. */
export const isConnectionFile = (path: string) => /^connections\/[^/]+\.yaml$/.test(path)

/** What `grants:` of the file says; `problem` is set when the text is not something this editor can read (then the YAML tab is the way). */
export function readGrants(text: string): { grants: Grants; problem: string | null } {
  const parsed = parseYaml(text)
  if (parsed.errors.length > 0) return { grants: noGrants, problem: 'The file is not valid YAML, so the grants cannot be shown here. Fix it in the YAML tab first.' }
  const section = (parsed.value as { grants?: unknown } | undefined)?.grants
  if (section === undefined || section === null) return { grants: noGrants, problem: null }
  if (typeof section !== 'object' || Array.isArray(section)) return { grants: noGrants, problem: '`grants` must be a mapping with `groups` and `everyone`. Fix it in the YAML tab.' }
  const { groups, everyone } = section as { groups?: unknown; everyone?: unknown }
  if (groups !== undefined && (!Array.isArray(groups) || groups.some((g) => typeof g !== 'string'))) return { grants: noGrants, problem: '`grants.groups` must be a list of group names. Fix it in the YAML tab.' }
  if (everyone !== undefined && typeof everyone !== 'boolean') return { grants: noGrants, problem: '`grants.everyone` must be true or false. Fix it in the YAML tab.' }
  return { grants: { groups: (groups as string[] | undefined) ?? [], everyone: everyone === true }, problem: null }
}

/**
 * The text with the grants set. Grants need `schema: connection/2`, so the file says it when it has any, and says `connection/1` again when the last is taken away (a program that reads only connection/1 then still reads the file).
 * Nothing else in the file changes.
 */
export function writeGrants(text: string, grants: Grants): string {
  if (grants.groups.length === 0 && !grants.everyone) {
    const without = removeAt(text, ['grants'])
    return setAt(without, ['schema'], 'connection/1')
  }
  const section: Record<string, unknown> = {}
  if (grants.everyone) section.everyone = true
  else section.groups = grants.groups
  return setAt(setAt(text, ['schema'], 'connection/2'), ['grants'], section)
}

/** What is wrong with a group name to add, as a sentence; null while it is empty or fine. */
export function groupProblem(name: string, existing: string[]): string | null {
  if (name === '') return null
  if (name !== name.trim() || [...name].some((c) => c.charCodeAt(0) < 32)) return 'A group name has no leading or trailing spaces and no control characters.'
  if (existing.some((g) => g.toLowerCase() === name.toLowerCase())) return `The group ${name} is listed already (names match ignoring case).`
  return null
}

export interface ConnectionGrantsViewProps {
  grants: Grants
  /** Set when the file's `grants` cannot be read by this editor. */
  problem: string | null
  draft: string
  readOnly: boolean
  onDraft: (name: string) => void
  onAdd: () => void
  onRemove: (name: string) => void
  onEveryone: (everyone: boolean) => void
}

/** The grants of a connection, without its state: who may use the data source once it is published. */
export function ConnectionGrantsView(p: ConnectionGrantsViewProps) {
  const problem = groupProblem(p.draft, p.grants.groups)
  const blocked = p.problem !== null || p.readOnly
  return (
    <div className="card" style={{ marginBottom: 14 }} data-testid="build-grants">
      <div className="card-head"><span className="card-title">Who may use it once published</span></div>
      <div className="card-body">
        <div className="hint" data-testid="build-grants-note">
          These are part of the definition published to a Gateway: a publish replaces the Gateway’s grants completely, and the Gateway adds none. Without any, no user of the Gateway’s identity home may use the data source.
          Save the file, then publish the connection again for a change to reach the Gateway.
        </div>
        {p.problem && <div className="hint" role="alert" style={{ marginTop: 6 }} data-testid="build-grants-problem">{p.problem}</div>}
        <label className="row" style={{ gap: 6, marginTop: 8 }}>
          <input type="checkbox" checked={p.grants.everyone} disabled={blocked} onChange={(e) => p.onEveryone(e.target.checked)} data-testid="build-grants-everyone" />
          Everyone who signs in to the Gateway (no group)
        </label>
        {!p.grants.everyone && (
          <div style={{ marginTop: 8 }}>
            {p.grants.groups.length === 0 && <div className="hint" data-testid="build-grants-none">No group is named: no one may use the published data source.</div>}
            {p.grants.groups.map((g) => (
              <div key={g} className="row" style={{ gap: 8, marginBottom: 4 }} data-testid={`build-grants-group-${g}`}>
                <span className="mono">{g}</span>
                <button type="button" className="btn btn-sm" disabled={blocked} onClick={() => p.onRemove(g)} data-testid={`build-grants-remove-${g}`}>Remove</button>
              </div>
            ))}
            <div className="row" style={{ gap: 8, marginTop: 6 }}>
              <input value={p.draft} disabled={blocked} onChange={(e) => p.onDraft(e.target.value)} placeholder="a group of the Gateway’s identity home" aria-label="Group" data-testid="build-grants-group-input" />
              <button type="button" className="btn" disabled={blocked || p.draft === '' || problem !== null} onClick={p.onAdd} data-testid="build-grants-add">Add the group</button>
            </div>
            {problem && <div className="hint" style={{ marginTop: 6 }} data-testid="build-grants-group-problem">{problem}</div>}
          </div>
        )}
      </div>
    </div>
  )
}

/** The grants of the open connection file, edited in the file's text (the form and the YAML tab show the same text). */
export function ConnectionGrantsEditor({ text, onText, readOnly = false }: { text: string; onText: (text: string) => void; readOnly?: boolean }) {
  const [draft, setDraft] = useState('')
  const { grants, problem } = readGrants(text)
  const change = (next: Grants) => { try { onText(writeGrants(text, next)) } catch { /* the text is not valid YAML: the problem line says so */ } }
  return (
    <ConnectionGrantsView
      grants={grants}
      problem={problem}
      draft={draft}
      readOnly={readOnly}
      onDraft={setDraft}
      onAdd={() => { change({ ...grants, groups: [...grants.groups, draft] }); setDraft('') }}
      onRemove={(name) => change({ ...grants, groups: grants.groups.filter((g) => g !== name) })}
      onEveryone={(everyone) => change(everyone ? { groups: [], everyone: true } : { ...grants, everyone: false })}
    />
  )
}
