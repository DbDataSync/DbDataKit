import type { AdminHost } from '@dbdatakit/admin'
import { CodeEditor } from './components/CodeEditor'
import { syncDocs } from './docs'
import { AppShell } from './components/AppShell'

/** What DbDataSync tells the shared admin screens: its frame, its code editor and its docs. */
export const adminHost: AdminHost = { Frame: AppShell, CodeEditor, docs: syncDocs }
