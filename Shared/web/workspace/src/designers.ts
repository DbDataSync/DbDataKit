import type { ComponentType } from 'react'

/**
 * A kind's Designer: its own screens over a draft, beside the Files area every kind has (`DbDataWorkspace/README.md`, "A project's two areas").
 * `Navigation` draws the kind's entries in the project's navigation; `Page` draws what is under `/:project/:draft/designer/` and routes the rest of
 * the address itself (`rest` is the address after `designer/`, empty for the Designer's landing page). Both read and write the draft through
 * `useDraft`, and link with `designerPath` and `filesPath`.
 */
export interface DesignerRegistration {
  kind: string
  /** The heading of the Designer's entries in the navigation. */
  title: string
  Navigation: ComponentType<DesignerProps>
  Page: ComponentType<DesignerProps>
}

/** What a Designer's components are drawn with: the draft's place, and the address after `designer/`. */
export interface DesignerProps { project: string; id: string; rest: string }

const designers = new Map<string, DesignerRegistration>()

/** Registers (or replaces) the Designer of a kind; a host calls it before the module is drawn. */
export function registerDesigner(designer: DesignerRegistration): void {
  designers.set(designer.kind, designer)
}

/** The Designer of a kind, undefined for a kind that has only Files. */
export function designerOf(kind: string | undefined): DesignerRegistration | undefined {
  return kind === undefined ? undefined : designers.get(kind)
}

/** Removes a kind's Designer (for tests). */
export function unregisterDesigner(kind: string): void {
  designers.delete(kind)
}

const enc = encodeURIComponent
const draftPath = (project: string, id: string) => `/${enc(project)}/${enc(id)}`

/** The address of a place in a draft's Designer, `rest` after `designer/` (each part escaped). */
export const designerPath = (project: string, id: string, rest = '') =>
  `${draftPath(project, id)}/designer${rest ? `/${rest.split('/').map(enc).join('/')}` : ''}`

/** The address of a file in a draft's Files area (no path: the area with no file open), with a tab of the area when given. */
export const filesPath = (project: string, id: string, path: string | null = null, tab?: string) =>
  `${draftPath(project, id)}/files${path ? `/${path.split('/').map(enc).join('/')}` : ''}${tab ? `?tab=${enc(tab)}` : ''}`

/** The address of a draft's Builds tab, where a build is made and published; with a build, that build is open with its publish panel. */
export const buildsPath = (project: string, id: string, build?: string) =>
  `${filesPath(project, id, null, 'builds')}${build ? `&build=${enc(build)}` : ''}`
