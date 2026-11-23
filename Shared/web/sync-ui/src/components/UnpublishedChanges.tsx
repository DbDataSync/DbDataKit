import type { ReactNode } from 'react'

/**
 * Where a draft stands against what was last published from it: the newest build of the draft a deploy put on a Runner's channel, how many commits
 * the draft has after the commit that build was made from, and the paths with an unsaved edit. A Runner runs the published version until the set is
 * built and published again; nothing is published on save.
 */
export interface PublishState {
  /** The newest deploy of a build of this draft to a Runner; null when none was made. */
  published: { build: string; version: string; channel: string; runner: string; mode: string } | null
  /** Commits of the draft after the build's commit; null when the build's commit is not in the draft's history (or not known yet). */
  commitsSince: number | null
  /** The draft's paths with an unsaved edit. */
  unsaved: readonly string[]
}

/** What a replication's page says is not published yet: commits of the draft after the build, unsaved edits of this replication, and of the files every replication uses. */
export interface Unpublished {
  commits: number
  unsavedHere: number
  unsavedShared: number
}

const sharedPath = (path: string) => path === 'catalog-cache.yaml' || (path.startsWith('config/') && !path.startsWith('config/replications/')) || path.startsWith('drivers/')

/**
 * What is not published of a replication, or null when nothing is or the draft was never published. Every commit counts for every replication: the
 * set is one package. An unsaved edit counts when it is this replication's file, or one every replication uses (connections, scripts, drivers,
 * the catalog cache); another replication's unsaved edit does not.
 */
export function unpublishedOf(state: PublishState | null | undefined, replication: string): Unpublished | null {
  if (!state?.published) return null
  const here = `config/replications/${replication}/`
  const unsavedHere = state.unsaved.filter((p) => p.startsWith(here)).length
  const unsavedShared = state.unsaved.filter(sharedPath).length
  const commits = state.commitsSince ?? 0
  return commits + unsavedHere + unsavedShared > 0 ? { commits, unsavedHere, unsavedShared } : null
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`

/** The rail's card that says a replication has changes not published yet. `children` is where the action to build and publish goes. */
export function UnpublishedNotice({ state, replication, children }: { state: PublishState | null | undefined; replication: string; children?: ReactNode }) {
  const what = unpublishedOf(state, replication)
  if (!what || !state?.published) return null
  const p = state.published
  const parts = [
    what.commits > 0 ? `${plural(what.commits, 'commit', 'commits')} of the draft after the build that was ${p.mode === 'serve' ? 'published' : 'staged'}` : '',
    what.unsavedHere > 0 ? `unsaved edits of ${plural(what.unsavedHere, 'file', 'files')} of this replication` : '',
    what.unsavedShared > 0 ? `unsaved edits of ${plural(what.unsavedShared, 'file', 'files')} every replication uses` : '',
  ].filter(Boolean)
  const saved = what.unsavedHere + what.unsavedShared > 0 ? ', and a build takes only what is saved and committed' : ''
  const text = `Version ${p.version} on ${p.channel} of ${p.runner} was built before ${parts.join(', and ')}. The Runner runs that version until the set is built and published again${saved}.`
  return (
    <div className="banner warn" role="status" data-testid="unpublished-changes" data-commits={what.commits} data-unsaved-here={what.unsavedHere} data-unsaved-shared={what.unsavedShared}>
      <div><strong>Unpublished changes.</strong> <span data-testid="unpublished-changes-text">{text}</span></div>
      {children && <div style={{ marginTop: 4 }}>{children}</div>}
    </div>
  )
}
