import type { AdminHost } from '@dbdatakit/admin'
import { ConsoleShell } from './ConsoleShell'

/** What the console tells the shared admin screens (users and invitations): its frame. */
export const adminHost: AdminHost = {
  Frame: ConsoleShell,
}
