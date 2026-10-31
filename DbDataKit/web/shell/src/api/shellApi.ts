import type { AboutInfo, AuthStatus, CreatedInvite, NotificationFeed, UserSummary } from './types'
import { put, request } from './http'

/** The calls every product's console makes the same way: who am I, sign in and out, invitations and users, and the notification feed. */
export const shellApi = {
  about: <T extends AboutInfo = AboutInfo>() => request<T>('/api/about'),
  auth: {
    status: () => request<AuthStatus>('/api/auth/status'),
    /** Negotiates a Windows identity and mints a session. Windows deployments only — the endpoint
     * says so where it is not one. */
    signInWithWindows: () => request<AuthStatus>('/api/auth/windows', { method: 'POST' }),
    /** Ends the session. A console that signs people in through an identity home answers with where to send the browser so the home ends its session too (`redirectTo`); otherwise there is no body. */
    signOut: () => request<{ redirectTo?: string } | undefined>('/api/auth/sign-out', { method: 'POST' }),
    beginPasskey: () => request<unknown>('/api/auth/passkey/begin', { method: 'POST' }),
    completePasskey: (assertion: unknown) =>
      request<AuthStatus>('/api/auth/passkey/complete', { method: 'POST', body: JSON.stringify(assertion) }),
  },
  invites: {
    create: (role: string, forUserId?: string) =>
      request<CreatedInvite>('/api/invites', { method: 'POST', body: JSON.stringify({ role, forUserId: forUserId ?? null }) }),
    check: (code: string) => request<{ valid: boolean }>(`/api/invites/check?code=${encodeURIComponent(code)}`),
    beginRegistration: (code: string, displayName: string, email: string | null) =>
      request<unknown>('/api/invites/begin-registration', { method: 'POST', body: JSON.stringify({ code, displayName, email }) }),
    completeRegistration: (code: string, displayName: string, email: string | null, attestation: unknown) =>
      request<AuthStatus>(
        `/api/invites/complete-registration?code=${encodeURIComponent(code)}` +
          `&displayName=${encodeURIComponent(displayName)}&email=${encodeURIComponent(email ?? '')}`,
        { method: 'POST', body: JSON.stringify(attestation) },
      ),
  },
  users: {
    list: () => request<UserSummary[]>('/api/users'),
    update: (id: string, changes: { role?: string; enabled?: boolean }) =>
      put<UserSummary>(`/api/users/${encodeURIComponent(id)}`, changes),
    removeCredential: (id: string, credentialId: string) =>
      request<void>(`/api/users/${encodeURIComponent(id)}/credentials/${encodeURIComponent(credentialId)}`, { method: 'DELETE' }),
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
