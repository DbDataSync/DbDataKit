import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { adminApi as api } from './adminApi'
import type { UpdatePhase } from './types'

// Query keys are centralized here so mutations know exactly what to invalidate.
const keys = {
  drivers: ['drivers'] as const,
  knownDriverKinds: ['known-driver-kinds'] as const,
  driverYaml: (id: string) => ['drivers', id, 'yaml'] as const,
  driverStatus: (id: string) => ['drivers', id, 'status'] as const,
  libraries: ['libraries'] as const,
  files: ['files'] as const,
  knownLibraries: ['known-libraries'] as const,
  knownDrivers: ['known-drivers'] as const,
  restartRequired: ['admin', 'restart-required'] as const,
  updateStatus: ['admin', 'update', 'status'] as const,
  updateReleases: (channel: string) => ['admin', 'update', 'releases', channel] as const,
  adminConfig: ['admin', 'config'] as const,
  adminCertificate: ['admin', 'certificate'] as const,
  adminCertificateCandidates: ['admin', 'certificate', 'candidates'] as const,
}

/** Every registered driver — the engine picker's data source, so a `driver.yaml` descriptor (phase
 * 109d) appears without a SPA change. Rarely if ever changes within a session, so no polling. */
export function useDrivers() {
  return useQuery({ queryKey: keys.drivers, queryFn: api.drivers.list })
}

/** The driver-authoring form's own capability checkboxes — never changes within a session (it's
 * derived from what this build of the server can construct), so no polling. */
export function useKnownDriverKinds() {
  return useQuery({ queryKey: keys.knownDriverKinds, queryFn: api.drivers.knownKinds })
}

/** The driver-authoring form's own "load for editing" — disabled until an id is actually given, so the
 * "new driver" case (no id yet) doesn't fire a request for `/api/drivers/undefined/yaml`. */
export function useDriverYaml(id: string | undefined) {
  return useQuery({
    queryKey: keys.driverYaml(id ?? ''), queryFn: () => api.drivers.getYaml(id!), enabled: !!id,
  })
}

/** Create — invalidates the drivers list and restart-required, the same pair every other
 * drivers/libraries mutation already invalidates. */
export function useCreateDriver() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (yaml: string) => api.drivers.create(yaml),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: keys.drivers })
      queryClient.invalidateQueries({ queryKey: keys.restartRequired })
    },
  })
}

/** Save an existing driver's yaml. */
export function useUpdateDriverYaml() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, yaml }: { id: string; yaml: string }) => api.drivers.updateYaml(id, yaml),
    onSuccess: (_data, { id }) => {
      queryClient.invalidateQueries({ queryKey: keys.drivers })
      queryClient.invalidateQueries({ queryKey: keys.driverYaml(id) })
      queryClient.invalidateQueries({ queryKey: keys.restartRequired })
    },
  })
}

/**
 * Phase 182N. Only worth asking when a connection's driver isn't already in `useDrivers()`'s list — see
 * `ConnectionEditPage`'s own call site. `staleTime: Infinity` matches `useCapabilities`' own reasoning:
 * whether a driver is registered doesn't change within one running API process.
 */
export function useDriverStatus(driverType: string | undefined) {
  return useQuery({
    queryKey: keys.driverStatus(driverType ?? ''),
    queryFn: () => api.drivers.status(driverType!),
    enabled: !!driverType,
    staleTime: Infinity,
  })
}

/** Phase 181N — validate-and-echo, on demand (not on every keystroke — see DriverEditPage's own "Validate"
 * button). A mutation, not a query: it's explicitly triggered, matching `useTestConnection`'s own shape,
 * and its result should never be treated as cached data to invalidate/refetch. */
export function useValidateDriverYaml() {
  return useMutation({ mutationFn: (yaml: string) => api.drivers.validate(yaml) })
}

/** Every installed library (phase 118's admin Libraries screen). Same call `useDrivers` already makes
 * in spirit — this list changes at most once per deployment, so no polling. */
export function useLibraries() {
  return useQuery({ queryKey: keys.libraries, queryFn: api.libraries.list })
}

/** The bundled, vetted libraries an "add" affordance offers (phase 117/118). */
export function useKnownLibraries() {
  return useQuery({ queryKey: keys.knownLibraries, queryFn: api.libraries.knownLibraries })
}

/** The bundled, ready-made driver descriptors an "add" affordance offers (phase 117/118). */
export function useKnownDrivers() {
  return useQuery({ queryKey: keys.knownDrivers, queryFn: api.libraries.knownDrivers })
}

/** The NuGet search box on the Libraries screen (phase 119) — a mutation, not a query: it runs once
 * per operator-initiated search rather than being cached against a query key, since the same term
 * typed twice should still hit the (possibly-changed) live index. */
export function useSearchLibraries() {
  return useMutation({ mutationFn: (q: string) => api.libraries.search(q) })
}

/** Installs a library from the Libraries screen (phase 120) — synchronous on the server, so this
 * resolves only once the restore actually finishes. */
export function useInstallLibrary() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { packageId: string; version: string; factoryType?: string; source?: string }) =>
      api.libraries.create(body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: keys.libraries })
      queryClient.invalidateQueries({ queryKey: keys.restartRequired })
    },
  })
}

/** Removes a library — `force: true` overrides the 409 a still-in-use one otherwise answers with. */
export function useRemoveLibrary() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, force }: { id: string; force?: boolean }) => api.libraries.remove(id, force),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: keys.libraries })
      queryClient.invalidateQueries({ queryKey: keys.restartRequired })
    },
  })
}

/** Every file in the `files/` store (phase 173V's admin Files screen). Changes only when an operator
 * uploads or removes one — no polling, the same posture `useLibraries` already takes. */
export function useFiles() {
  return useQuery({ queryKey: keys.files, queryFn: api.files.list })
}

/** Uploads one or more files. No restart-required invalidation — unlike a library or driver, a file
 * alone changes nothing already loaded; only a driver.yaml naming it does, and that's its own action. */
export function useUploadFiles() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (files: FileList | File[]) => api.files.upload(files),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.files }),
  })
}

/** Removes a file — `force: true` overrides the 409 a still-in-use one otherwise answers with. */
export function useRemoveFile() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ name, force }: { name: string; force?: boolean }) => api.files.remove(name, force),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: keys.files }),
  })
}

/** The Drivers screen's one-click "Add" from a bundled catalog entry (phase 120). */
export function useInstallDriverFromCatalog() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ knownDriverId, version }: { knownDriverId: string; version: string }) =>
      api.drivers.installFromCatalog(knownDriverId, version),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: keys.drivers })
      queryClient.invalidateQueries({ queryKey: keys.libraries })
      queryClient.invalidateQueries({ queryKey: keys.restartRequired })
    },
  })
}

/** Whether this process needs a restart to pick up a change — a config value (phase 81), or (phase
 * 120) a library or driver installed/removed through the web console. Not polled: the mutations above
 * (and useSetAdminConfig/useSetAdminConfigSecret) invalidate this explicitly, the same caution every
 * other host-state query here already takes. */
export function useRestartRequired() {
  return useQuery({ queryKey: keys.restartRequired, queryFn: api.admin.restartRequired.get })
}

/** The phases in which an update is in flight — recorded by `dbdatasync update --apply` as it installs into the other
 * slot and switches to it. `staging`/`draining` are phase 159's, kept so an old state file still reads. */
export const ACTIVE_UPDATE_PHASES: readonly UpdatePhase[] = ['staging', 'draining', 'applying', 'restarting']

/**
 * Where the update stands. Polls while one is in flight — and **keeps polling through the failures a restart
 * causes**: the CLI stops and starts the service while it switches slots, so for a few seconds every request fails,
 * and the last known phase is still an active one. `retry: false` so a request that fails during that gap is a data
 * point to try again in two seconds, not something for react-query to back off from.
 */
export function useUpdateStatus() {
  return useQuery({
    queryKey: keys.updateStatus,
    queryFn: api.admin.update.status,
    retry: false,
    refetchInterval: (query) => {
      const phase = query.state.data?.phase
      return phase && ACTIVE_UPDATE_PHASES.includes(phase) ? 2_000 : false
    },
  })
}

/** Releases on one channel, read from the pinned sources when asked (`enabled`) — never polled: an air-gapped
 * install must not start making outbound calls because a page is open. */
export function useUpdateReleases(channel: string | undefined, enabled: boolean) {
  return useQuery({
    queryKey: keys.updateReleases(channel ?? ''),
    queryFn: () => api.admin.update.releases(channel!),
    enabled: enabled && !!channel,
    retry: false,
    staleTime: 60_000,
  })
}

/** Every DbDataSync:* key docs/configuration.md documents — the admin config screen (phase 81). Not polled: this is
 * process configuration, not something that changes underneath an open tab. */
export function useAdminConfig() {
  return useQuery({ queryKey: keys.adminConfig, queryFn: api.admin.config.list })
}

/** Writes one key into dbdatasync.config.yaml — a direct edit of a file-sourced row, or "adopt" of one
 * that is not. Same mutation either way; the page decides which value to send. */
export function useSetAdminConfig() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => api.admin.config.set(key, value),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: keys.adminConfig })
      queryClient.invalidateQueries({ queryKey: keys.restartRequired })
    },
  })
}

/** StateConnectionString's password, through the secret store. A separate mutation from the value
 * above because it never touches dbdatasync.config.yaml and the page never sees what it sets. */
export function useSetAdminConfigSecret() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) => api.admin.config.setSecret(key, value),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: keys.adminConfig })
      queryClient.invalidateQueries({ queryKey: keys.restartRequired })
    },
  })
}

/** The bound certificate, its expiry, binding state and key access (phase 83). Not polled, the same
 * reasoning as useAdminConfig: this is process/host state, not something that changes underneath an
 * open tab on its own — an action below invalidates it explicitly instead. */
export function useAdminCertificate() {
  return useQuery({ queryKey: keys.adminCertificate, queryFn: api.admin.certificate.get })
}

/** Server-auth certificates already in LocalMachine\My — what the Bind dialog picks from. Fetched only
 * while that dialog is open (`enabled`), since listing a certificate store is not free and nothing else
 * on this screen needs it. */
export function useAdminCertificateCandidates(enabled: boolean) {
  return useQuery({
    queryKey: keys.adminCertificateCandidates,
    queryFn: api.admin.certificate.candidates,
    enabled,
  })
}

/** Every certificate action below invalidates the same query: each one changes what GET reports (a new
 * certificate installed, a pending enrollment recorded or resolved, a binding written), and the API's
 * response already carries the refreshed status — invalidating still triggers a real refetch rather than
 * quietly trusting a POST body forever, the same caution useSetAdminConfig already takes. */
function invalidateAdminCertificate(queryClient: ReturnType<typeof useQueryClient>) {
  queryClient.invalidateQueries({ queryKey: keys.adminCertificate })
}

export function useCreateSelfSignedCertificate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ dnsNames, validityDays }: { dnsNames: string[]; validityDays: number | null }) =>
      api.admin.certificate.createSelfSigned(dnsNames, validityDays),
    onSuccess: () => invalidateAdminCertificate(queryClient),
  })
}

export function useEnrollCertificate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ dnsNames, template, caConfig }: { dnsNames: string[]; template: string; caConfig: string | null }) =>
      api.admin.certificate.enroll(dnsNames, template, caConfig),
    onSuccess: () => invalidateAdminCertificate(queryClient),
  })
}

export function useRetrieveCertificate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (requestId: string) => api.admin.certificate.retrieve(requestId),
    onSuccess: () => invalidateAdminCertificate(queryClient),
  })
}

export function useBindCertificate() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ thumbprint, allowInvalid }: { thumbprint: string; allowInvalid: boolean | null }) =>
      api.admin.certificate.bind(thumbprint, allowInvalid),
    onSuccess: () => {
      invalidateAdminCertificate(queryClient)
      queryClient.invalidateQueries({ queryKey: keys.adminCertificateCandidates })
    },
  })
}
