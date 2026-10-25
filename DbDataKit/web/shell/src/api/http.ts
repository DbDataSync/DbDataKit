// The one place the apps' calls leave from: same-origin, JSON, errors as ApiError.

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

let apiBase = ''

/**
 * Where this app's `/api/...` calls go. A product's own app calls its own server (`''`); a module hosted by the console calls the server it manages through the console's relay
 * (`/relay/<server>`). The sign-in and notification calls always go to the host the app is served by, whatever the base: who is signed in is the console's answer, not a managed server's.
 */
export function setApiBase(base: string) {
  apiBase = base.replace(/\/+$/, '')
}

/** The base now in effect; a module being left puts back the base it found, but only if nobody has set another since. */
export function currentApiBase() {
  return apiBase
}

const HOST_OWN = ['/api/auth', '/api/notifications']

export function apiUrl(path: string): string {
  if (!apiBase || !path.startsWith('/api/') || HOST_OWN.some((own) => path === own || path.startsWith(own + '/') || path.startsWith(own + '?'))) return path
  return apiBase + path
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(apiUrl(path), {
    ...init,
    // The session is a cookie, and a cross-origin dev setup (Vite on 5173 proxying to the API) does
    // not send one unless asked. Without this, signing in appears to do nothing.
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })

  if (!response.ok) {
    let message = response.statusText
    try {
      const body = await response.json()
      message = body.error ?? body.title ?? message
    } catch {
      // No JSON body — fall back to the status text already captured.
    }
    throw new ApiError(response.status, message)
  }

  if (response.status === 204) return undefined as T
  const text = await response.text()
  return text ? (JSON.parse(text) as T) : (undefined as T)
}

export const put = <T>(path: string, body: unknown) =>
  request<T>(path, { method: 'PUT', body: JSON.stringify(body) })
