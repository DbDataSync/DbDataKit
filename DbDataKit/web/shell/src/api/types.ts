/** Who the caller is, and how they could sign in. The one endpoint the app can always call — the
 * answer to "am I signed in" cannot itself require being signed in. */
export interface AuthStatus {
  authenticated: boolean
  userId: string | null
  displayName: string | null
  /** `Admin` or `Viewer`. Null when nobody is signed in, and on a deployment that has deliberately
   * turned authentication off — where everything is permitted and nobody has a name. */
  role: string | null
  /** Sign-in methods this deployment offers, so the sign-in screen shows the ones that exist. */
  methods: string[]
}

/**
 * One notification from the global feed — see phase 77.
 *
 * `kind` is an open set: the server may write a kind this build has never heard of, which is why
 * nothing keys rendering off it. `message` is what the server said at the time, stored rather than
 * recomposed, so an old row still reads correctly after the run it describes has been pruned.
 */
export interface NotificationRecord {
  id: number
  kind: string
  createdAtUtc: string
  taskName: string | null
  mappingName: string | null
  runId: string | null
  message: string
}

/**
 * `personalized` is false where the deployment does not authenticate: there is no user to key a read
 * cursor to, so everything reads as unread permanently. The bell says so rather than offering a
 * dismissal that would not stick.
 */
export interface NotificationFeed {
  notifications: NotificationRecord[]
  lastSeenNotificationId: number | null
  unreadCount: number
  personalized: boolean
}
