import type { AdminHost } from '@dbdatakit/admin'
import { CodeEditor } from './components/CodeEditor'
import { DOC_PAGES } from './components/docPages'
import { AppShell } from './components/AppShell'

/** What DbDataSync tells the shared admin screens: its frame, its code editor and the docs that shipped with it. */
export const adminHost: AdminHost = { Frame: AppShell, CodeEditor, docPages: DOC_PAGES }
