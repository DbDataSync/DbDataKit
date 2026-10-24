import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { shellApi } from './shellApi'

const keys = {
  notifications: ['notifications'] as const,
}

/**
 * The notification feed, polled — see phase 77.
 *
 * Polled rather than pushed: the run hub exists for somebody watching one run live, and a bell is the
 * opposite case, a person who is not looking. Fifteen seconds is well inside the interval at which
 * anybody notices, and one small query at that rate is nothing next to the scheduler's own tick.
 */
export function useNotifications() {
  return useQuery({
    queryKey: keys.notifications,
    queryFn: () => shellApi.notifications.list(),
    refetchInterval: 15_000,
    refetchOnWindowFocus: true,
  })
}

/** Advances the read cursor. Only ever moves forward, which the server enforces. */
export function useMarkNotificationsSeen() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (lastSeenNotificationId: number) => shellApi.notifications.markSeen(lastSeenNotificationId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.notifications }),
  })
}

/**
 * Who the caller is. Refetched on window focus, so a session that expired while a tab was in the
 * background is noticed when somebody comes back to it rather than at their next click.
 */
export function useAuthStatus() {
  return useQuery({
    queryKey: ['auth', 'status'] as const,
    queryFn: () => shellApi.auth.status(),
    refetchOnWindowFocus: true,
    retry: false,
  })
}

export function useSignInWithWindows() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => shellApi.auth.signInWithWindows(),
    // Everything on screen was fetched as nobody; none of it is right any more.
    onSuccess: () => queryClient.invalidateQueries(),
  })
}

export function useSignOut() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => shellApi.auth.signOut(),
    onSuccess: () => queryClient.invalidateQueries(),
  })
}
