// The unsaved edits of a Sync draft that a question to the Workspace carries (the `sync/` routes and `provisioning-plan`), so the answer reads the
// draft as the person sees it. Both areas of a sync project ask with them: the Designer's tabs and the Files area's Provisioning tab.

/** The draft's unsaved edits a question carries, so the answer reads the draft as the person sees it: `catalog-cache.yaml`, files under `config/` and `drivers/<folder>/driver.yaml`. */
export type Unsaved = Record<string, string | null>

/** The paths whose unsaved text a question carries. */
export const isQuestionPath = (path: string): boolean =>
  path === 'catalog-cache.yaml' || path.startsWith('config/') || /^drivers\/[^/]+\/driver\.yaml$/.test(path)

/** The most edits a question carries (the route refuses more). */
export const MAX_UNSAVED_FILES = 500

/** The unsaved edits of the draft that a question carries, in path order, from the frame's dirty paths and their texts. */
export function unsavedOf(dirtyPaths: readonly string[], textOf: (path: string) => string | undefined): Unsaved {
  const out: Unsaved = {}
  for (const path of [...dirtyPaths].filter(isQuestionPath).sort().slice(0, MAX_UNSAVED_FILES)) {
    const text = textOf(path)
    if (text !== undefined) out[path] = text
  }
  return out
}
