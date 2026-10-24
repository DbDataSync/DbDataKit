import type { AuthStatus, NotificationFeed } from './types'
import { request } from './http'

/** The calls every product's console makes the same way: who am I, sign in and out, and the notification feed. */
export const shellApi = {
  auth: {
    status: () => request<AuthStatus>('/api/auth/status'),
    /** Negotiates a Windows identity and mints a session. Windows deployments only — the endpoint
     * says so where it is not one. */
    signInWithWindows: () => request<AuthStatus>('/api/auth/windows', { method: 'POST' }),
    signOut: () => request<void>('/api/auth/sign-out', { method: 'POST' }),
    beginPasskey: () => request<unknown>('/api/auth/passkey/begin', { method: 'POST' }),
    completePasskey: (assertion: unknown) =>
      request<AuthStatus>('/api/auth/passkey/complete', { method: 'POST', body: JSON.stringify(assertion) }),
  },
  notifications: {
    /** The whole feed by default. `sinceId` exists for a future notification centre that pages
     * through it; the bell wants the latest and asks for all of it, which retention bounds. */
    list: (sinceId?: number) =>
      request<NotificationFeed>(`/api/notifications${sinceId ? `?sinceId=${sinceId}` : ''}`),
    markSeen: (lastSeenNotificationId: number) =>
      request<NotificationFeed>('/api/notifications/seen', {
        method: 'POST',
        body: JSON.stringify({ lastSeenNotificationId }),
      }),
  },
}
