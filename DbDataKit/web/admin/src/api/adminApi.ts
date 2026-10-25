import { ApiError, apiUrl, put, request } from '@dbdatakit/shell'
import type {
  AdminCertificateStatus,
  AdminConfigEntry,
  CertificateActionResult,
  CertificateCandidate,
  DriverKindsSummary,
  DriverStatusResult,
  DriverSummary,
  DriverType,
  DriverValidationResult,
  DriverYaml,
  FileSummary,
  FromCatalogResult,
  KnownDriverSummary,
  KnownLibrarySummary,
  LibraryManifest,
  LibrarySearchResponse,
  LibrarySummary,
  ParameterDescriptor,
  RestartRequiredStatus,
  UpdateReleases,
  UpdateStatus,
  UploadResult,
} from './types'

/** A page of the docs the build shipped (phase 160), as Markdown text. Not `request`: the answer is a document, not
 * JSON. A page that is not there is a 404, but the content type is checked as well, so that anything other than
 * Markdown — an HTML error page from a proxy in front of the app — is refused rather than rendered as a document. */
async function docPage(slug: string): Promise<string> {
  const response = await fetch(`/docs/${encodeURIComponent(slug)}.md`, { credentials: 'same-origin' })
  if (!response.ok) throw new ApiError(response.status, response.status === 404 ? 'That page is not part of this build.' : response.statusText)
  if (!response.headers.get('content-type')?.toLowerCase().startsWith('text/markdown'))
    throw new ApiError(response.status, 'The server did not answer with a document.')
  return response.text()
}

/** The calls of the admin screens, in the shape `/api/...` has them in every product that hosts these screens. */
export const adminApi = {
  docs: { page: docPage },
  admin: {
    config: {
      list: () => request<AdminConfigEntry[]>('/api/admin/config'),
      /** Writes one key into dbdatasync.config.yaml — the same call for a direct edit of a file-sourced
       * key and an "adopt" of one that is not; the caller supplies the current effective value either
       * way. Does not take effect in the running process until it restarts. */
      set: (key: string, value: string) =>
        put<AdminConfigEntry>(`/api/admin/config/${encodeURIComponent(key)}`, { value }),
      /** StateConnectionString's password only, through the secret store — never through the file. */
      setSecret: (key: string, value: string) =>
        request<void>(`/api/admin/config/${encodeURIComponent(key)}/secret`, {
          method: 'PUT', body: JSON.stringify({ value }),
        }),
    },
    /** The Certificates section (phase 83) — a second door onto `dbdatasync cert …`. None of these ever
     * return or accept a private key. */
    certificate: {
      get: () => request<AdminCertificateStatus>('/api/admin/certificate'),
      candidates: () => request<CertificateCandidate[]>('/api/admin/certificate/candidates'),
      createSelfSigned: (dnsNames: string[], validityDays: number | null) =>
        request<CertificateActionResult>('/api/admin/certificate/self-signed', {
          method: 'POST', body: JSON.stringify({ dnsNames, validityDays }),
        }),
      enroll: (dnsNames: string[], template: string, caConfig: string | null) =>
        request<CertificateActionResult>('/api/admin/certificate/enroll', {
          method: 'POST', body: JSON.stringify({ dnsNames, template, caConfig }),
        }),
      retrieve: (requestId: string) =>
        request<CertificateActionResult>('/api/admin/certificate/retrieve', {
          method: 'POST', body: JSON.stringify({ requestId }),
        }),
      bind: (thumbprint: string, allowInvalid: boolean | null) =>
        request<CertificateActionResult>('/api/admin/certificate/bind', {
          method: 'POST', body: JSON.stringify({ thumbprint, allowInvalid }),
        }),
    },
    /** Whether this process has a config value, library, or driver change on disk it hasn't picked up
     * yet (phase 120) — a server-side flag, not just this tab's own session state, so a different
     * admin's tab (or this one after a reload) still learns about a change another request made. */
    restartRequired: {
      get: () => request<RestartRequiredStatus>('/api/admin/restart-required'),
    },
    /** The Updates screen (phase 159; read-only since 196L — an update is applied from a shell). Every call is admin-only. */
    update: {
      status: () => request<UpdateStatus>('/api/admin/update/status'),
      /** Read from the pinned release sources on each call — nothing polls them in the background. */
      releases: (channel: string) =>
        request<UpdateReleases>(`/api/admin/update/releases?channel=${encodeURIComponent(channel)}&limit=15`),
    },
  },
  libraries: {
    /** Every installed library (phase 118's admin Libraries screen). Admin-only, unlike `drivers.list`
     * below — this reveals what's on disk, not just what a connection editor picker offers. */
    list: () => request<LibrarySummary[]>('/api/libraries'),
    knownLibraries: () => request<KnownLibrarySummary[]>('/api/known-libraries'),
    knownDrivers: () => request<KnownDriverSummary[]>('/api/known-drivers'),
    /** A read-only proxy onto the public NuGet index (phase 119). A disabled deployment answers 503,
     * which `request` surfaces as a thrown `ApiError` — the caller treats that the same as a
     * `status: "unavailable"` body (a 200 the call still resolved with), both meaning "fall back to
     * manual entry". */
    search: (q: string) => request<LibrarySearchResponse>(`/api/libraries/search?q=${encodeURIComponent(q)}`),
    /** Installs synchronously — the request holds until `dotnet publish` finishes restoring the
     * package (phase 120). `factoryType` is required only when `packageId` isn't a curated one. */
    create: (body: { packageId: string; version: string; factoryType?: string; source?: string }) =>
      request<LibraryManifest>('/api/libraries', { method: 'POST', body: JSON.stringify(body) }),
    /** Refused (409) while a driver still names this library, unless `force`. */
    remove: (id: string, force?: boolean) =>
      request<void>(`/api/libraries/${encodeURIComponent(id)}${force ? '?force=true' : ''}`, { method: 'DELETE' }),
  },
  files: {
    /** Every file in the `files/` store (phase 173V's admin Files screen). */
    list: () => request<FileSummary[]>('/api/files'),
    /** `request` always JSON-encodes its body, which a file upload can't use — a dedicated call that
     * hands the browser a `FormData` body and lets it set `Content-Type: multipart/form-data;
     * boundary=...` itself (setting that header by hand breaks the boundary the browser would have
     * chosen). One `UploadResult` per file, not all-or-nothing over a single stale name. */
    upload: async (files: FileList | File[]): Promise<UploadResult[]> => {
      const form = new FormData()
      for (const file of files) form.append('files', file)
      const response = await fetch(apiUrl('/api/files'), { method: 'POST', credentials: 'same-origin', body: form })
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
      return (await response.json()) as UploadResult[]
    },
    /** Refused (409) while a driver still names this file, unless `force`. */
    remove: (name: string, force?: boolean) =>
      request<void>(`/api/files/${encodeURIComponent(name)}${force ? '?force=true' : ''}`, { method: 'DELETE' }),
  },
  drivers: {
    /** Every registered driver — the three built-ins plus any `driver.yaml` descriptor an operator has
     * added (phase 109d). What the connection editor's engine picker reads instead of a hardcoded list. */
    list: () => request<DriverSummary[]>('/api/drivers'),
    /** What a connection of this driver takes, given what it has been given so far. A POST because
     * the answer depends on the values — Host is not a setting in connection-string mode — and those
     * are an arbitrary operator-typed bag that does not belong in a query string. */
    connectionParameters: (driverType: DriverType, values: Record<string, string>) =>
      request<ParameterDescriptor[]>(
        `/api/drivers/${encodeURIComponent(driverType)}/connection-parameters`,
        { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(values) },
      ),
    /** The one-click "add" from a bundled `KnownDrivers` catalog entry (phase 120) — installs its
     * bound library (reusing it if already installed) and writes the descriptor. */
    installFromCatalog: (knownDriverId: string, version: string) =>
      request<FromCatalogResult>('/api/drivers/from-catalog', {
        method: 'POST', body: JSON.stringify({ knownDriverId, version }),
      }),
    /** The driver-authoring form's own capability checkboxes — sourced from the server rather than
     * hardcoded here, since `GenericDriverBase`'s own reader/staging/writer construction is the real
     * source of truth for which Kind strings are valid. */
    knownKinds: () => request<DriverKindsSummary>('/api/known-driver-kinds'),
    /** The driver-authoring form's own load-for-editing — the raw file. */
    getYaml: (id: string) => request<DriverYaml>(`/api/drivers/${encodeURIComponent(id)}/yaml`),
    /** Phase 182N — why a driver isn't registered, for `ConnectionEditPage`'s own broken-driver banner.
     * Cheap: only called for an id that isn't already in `list()`'s own result. */
    status: (id: string) => request<DriverStatusResult>(`/api/drivers/${encodeURIComponent(id)}/status`),
    /** Create: validated entirely server-side before anything is written — a 400 carries the real
     * parse/build error, a 409 means the id already exists. */
    create: (yaml: string) =>
      request<DriverYaml>('/api/drivers', { method: 'POST', body: JSON.stringify({ yaml }) }),
    /** Save an existing driver's yaml. The yaml's own `id:` must still match `id` — renaming isn't
     * supported here (400 otherwise). */
    updateYaml: (id: string, yaml: string) =>
      request<DriverYaml>(`/api/drivers/${encodeURIComponent(id)}/yaml`, { method: 'PUT', body: JSON.stringify({ yaml }) }),
    /** Phase 181N — validate-and-echo, entirely in memory, never writing to disk. Viewer-reachable,
     * unlike `create`/`updateYaml`, matching every other endpoint the driver/connection editors call. */
    validate: (yaml: string) =>
      request<DriverValidationResult>('/api/drivers/validate', { method: 'POST', body: JSON.stringify({ yaml }) }),
  },}
