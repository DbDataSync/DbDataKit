import { createContext, useContext, type ComponentType, type ReactNode } from 'react'
import type { Crumb } from '@dbdatakit/shell'

/** The frame a product puts around a screen: its rail, crumbs and tab strip. The admin screens render inside whichever one the product gives them. */
export type AdminFrame = ComponentType<{ crumbs: Crumb[]; tabs?: ReactNode; actions?: ReactNode; children: ReactNode }>

/** The code editor the product uses; the driver form needs it for the `driver.yaml` text. */
export type AdminCodeEditor = ComponentType<{
  value: string
  language: 'yaml'
  onChange: (value: string) => void
  readOnly?: boolean
  minLines?: number
  maxLines?: number
  testId?: string
}>

/** What the product hosting the admin screens tells them about itself. */
export interface AdminHost {
  Frame: AdminFrame
  CodeEditor: AdminCodeEditor
  /** The pages of the docs that shipped with the build, in the order the index lists them. */
  docPages: readonly { slug: string; title: string }[]
}

const HostContext = createContext<AdminHost | null>(null)

export function AdminHostProvider({ host, children }: { host: AdminHost; children: ReactNode }) {
  return <HostContext.Provider value={host}>{children}</HostContext.Provider>
}

/** What the hosting product provided. A screen rendered outside `AdminHostProvider` is a bug in the product, so it fails loudly. */
export function useAdminHost(): AdminHost {
  const host = useContext(HostContext)
  if (!host) throw new Error('An admin screen was rendered outside AdminHostProvider.')
  return host
}
