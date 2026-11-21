import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError, Field, useIsAdmin } from '@dbdatakit/shell'
import { consoleApi, instancesKey, joinTokensKey, type JoinTokenMade, type WaitingJoinToken } from '../api'
import { ConsoleShell } from '../ConsoleShell'
import { emptyInviteForm, endsIn, inviteProblems, inviteRequest, joinProducts, joinRoles, MAX_MINUTES, productLabel, toggleRole, type InviteForm } from '../joinTokens'

/** How often the waiting tokens are asked for again, so a token a server has just spent leaves the list without anyone pressing anything. */
export const WAITING_REFRESH_MS = 5_000

const COLUMNS = '1fr 1.6fr 1fr 1.4fr 1fr 0.8fr'

/** What the person reads when making or cancelling a token failed: the console's own words, or a plain sentence for what it did not say. */
export function tokenFailure(error: unknown): string | null {
  if (!error) return null
  if (error instanceof ApiError) {
    if (error.status === 401 || error.status === 403) return 'Only administrators can add a server.'
    return error.message
  }
  return 'The console did not answer. Try again.'
}

export interface InviteFormViewProps {
  form: InviteForm
  /** Whether the person has pressed Make once: problems are shown from then on, not while the form is first filled in. */
  tried: boolean
  making: boolean
  failure: string | null
  onChange: (form: InviteForm) => void
  onMake: () => void
}

/** The form that makes a join token: which server, the most the console may do there, the group, how long the command works and the address the server reaches the console at. */
export function InviteFormView({ form, tried, making, failure, onChange, onMake }: InviteFormViewProps) {
  const problems = inviteProblems(form)
  const shown = tried ? problems : {}
  const problem = (field: keyof InviteForm) => shown[field] ? <div className="hint error" data-testid={`add-server-problem-${field}`}>{shown[field]}</div> : null
  return (
    <div className="card" data-testid="add-server-form">
      <div className="card-head"><span className="card-title">Make the command</span></div>
      <div className="card-body">
        <span className="hint">The console makes a single-use token and the command that joins the server with it. Run the command on the server&apos;s machine as an administrator; the server then joins at its next start, and the console pins its certificate.</span>
        {failure && <div className="banner error" role="alert" data-testid="add-server-error"><span className="mark">!</span><span>{failure}</span></div>}
        <Field label="Server">
          <span className="row" style={{ gap: 14 }} role="radiogroup" aria-label="Server">
            {joinProducts.map((p) => (
              <label key={p.id} className="row" style={{ gap: 6 }}>
                <input type="radio" name="add-server-product" value={p.id} checked={form.product === p.id} onChange={() => onChange({ ...form, product: p.id })} data-testid={`add-server-product-${p.id}`} />
                {p.label}
              </label>
            ))}
          </span>
          {problem('product')}
        </Field>
        <Field label="Role ceiling">
          <span className="hint">The most the console may do on that server; a person&apos;s own role there is never more than this.</span>
          <span className="row" style={{ gap: 14, flexWrap: 'wrap' }}>
            {joinRoles.map((role) => (
              <label key={role} className="row" style={{ gap: 6 }}>
                <input type="checkbox" checked={form.roles.includes(role)} onChange={(e) => onChange({ ...form, roles: toggleRole(form.roles, role, e.target.checked) })} data-testid={`add-server-role-${role}`} />
                {role}
              </label>
            ))}
          </span>
          {problem('roles')}
        </Field>
        <Field label="Group (optional)">
          <input className="input" placeholder="sales" value={form.group} onChange={(e) => onChange({ ...form, group: e.target.value })} data-testid="add-server-group-input" />
          {problem('group')}
        </Field>
        <Field label="Works for (minutes)">
          <input className="input" inputMode="numeric" value={form.minutes} onChange={(e) => onChange({ ...form, minutes: e.target.value })} data-testid="add-server-minutes-input" />
          <span className="hint">From 1 to {MAX_MINUTES}. The command works once, within this time.</span>
          {problem('minutes')}
        </Field>
        <Field label="The console's address, as the server's machine reaches it">
          <input className="input mono" value={form.url} onChange={(e) => onChange({ ...form, url: e.target.value })} data-testid="add-server-url-input" />
          {problem('url')}
        </Field>
        <button type="button" className="btn btn-primary" disabled={making || (tried && Object.keys(problems).length > 0)} onClick={onMake} data-testid="add-server-make-button">
          {making ? 'Making…' : 'Make the command'}
        </button>
      </div>
    </div>
  )
}

/** The command just made, shown this once: the console keeps no copy of the token in it. */
export function MadeCommandView({ made, now, onCopy }: { made: JoinTokenMade; now: number; onCopy: (command: string) => void }) {
  const program = joinProducts.find((p) => p.id === made.product)?.label.toLowerCase() ?? made.product
  return (
    <div className="card" style={{ marginTop: 20 }} data-testid="add-server-made">
      <div className="card-head"><span className="card-title">Run this on the {program}&apos;s machine</span></div>
      <div className="card-body">
        <span className="hint" data-testid="add-server-made-note">
          As an administrator, before {new Date(made.expires).toLocaleString()} ({endsIn(made.expires, now)}). It works once. This page shows the command only now: copy it before you leave.
        </span>
        <div className="row" style={{ gap: 8, alignItems: 'flex-start' }}>
          <textarea className="input mono" readOnly rows={3} value={made.command} onFocus={(e) => e.currentTarget.select()} style={{ flex: 1 }} data-testid="add-server-command" />
          <button type="button" className="btn" onClick={() => onCopy(made.command)} data-testid="add-server-copy-button">Copy</button>
        </div>
        {made.loopback && <div className="hint" data-testid="add-server-loopback">The command names a loopback address, which only a server on the console&apos;s own machine reaches. For another machine, make it again with the address that machine reaches the console at.</div>}
        <span className="hint">When the server has joined, it is listed under <Link to="/instances">All servers</Link> and its token leaves the list below.</span>
      </div>
    </div>
  )
}

export interface WaitingTokensViewProps {
  tokens: WaitingJoinToken[] | undefined
  loading: boolean
  listError: string | null
  /** The id of the token a cancel is under way for. */
  busy: string | null
  failure: string | null
  now: number
  onCancel: (id: string) => void
}

/** The tokens made and not yet used, with who made each and when it ends; Cancel ends one, so its command no longer joins anything. No token's value is here. */
export function WaitingTokensView({ tokens, loading, listError, busy, failure, now, onCancel }: WaitingTokensViewProps) {
  return (
    <div className="card" style={{ marginTop: 20 }} data-testid="add-server-waiting">
      <div className="card-head"><span className="card-title">Waiting to be used</span></div>
      <div className="card-body">
        <span className="hint">Each command made here or with <span className="mono">dbdatakit setup invite</span> until a server uses it or it ends. Cancel one you no longer need: the command made with it then joins nothing.</span>
        {(listError ?? failure) && <div className="banner error" role="alert" data-testid="add-server-waiting-error"><span className="mark">!</span><span>{listError ?? failure}</span></div>}
      </div>
      <div className="card flush">
        {loading && <div className="empty">Loading…</div>}
        {!loading && !listError && (tokens ?? []).length === 0 && <div className="empty" data-testid="add-server-waiting-empty">No token is waiting.</div>}
        {(tokens ?? []).length > 0 && (
          <div className="grid-head" style={{ gridTemplateColumns: COLUMNS, gap: 14 }}>
            <span>Server</span><span>Role ceiling</span><span>Group</span><span>Made by</span><span>Ends</span><span></span>
          </div>
        )}
        {(tokens ?? []).map((token) => (
          <div key={token.id} className="grid-row" style={{ gridTemplateColumns: COLUMNS, gap: 14 }} data-testid={`waiting-token-${token.id}`}>
            <span>{productLabel(token.product)}</span>
            <span className="mono">{token.roles.join(', ')}</span>
            <span>{token.group ?? '—'}</span>
            <span title={token.madeAt}>{token.madeBy}</span>
            <span title={token.expires} data-testid="waiting-token-ends">{endsIn(token.expires, now)}{token.tries > 0 ? <span className="hint"> ({token.tries} refused)</span> : null}</span>
            <span>
              <button type="button" className="btn" disabled={busy === token.id} onClick={() => onCancel(token.id)} data-testid="waiting-token-cancel-button">{busy === token.id ? 'Cancelling…' : 'Cancel'}</button>
            </span>
          </div>
        ))}
      </div>
    </div>
  )
}

/**
 * Add a server: an administrator makes the command a Gateway, a Runner or a Workspace joins the console with, copies it to that server's machine, and
 * sees the tokens still waiting, each of which they can cancel. The same token as `dbdatakit setup invite` makes.
 *
 * Design: docs/install.md#pairing
 */
export function AddServerPage() {
  const isAdmin = useIsAdmin()
  const queryClient = useQueryClient()
  const [form, setForm] = useState<InviteForm>(() => emptyInviteForm(window.location.origin))
  const [tried, setTried] = useState(false)
  const waiting = useQuery({ queryKey: joinTokensKey, queryFn: consoleApi.joinTokens.waiting, enabled: isAdmin, refetchInterval: WAITING_REFRESH_MS })
  const make = useMutation({
    mutationFn: () => consoleApi.joinTokens.make(inviteRequest(form)),
    onSuccess: () => {
      setTried(false)
      void queryClient.invalidateQueries({ queryKey: joinTokensKey })
    },
  })
  const cancel = useMutation({
    mutationFn: (id: string) => consoleApi.joinTokens.cancel(id),
    onSettled: () => queryClient.invalidateQueries({ queryKey: joinTokensKey }),
  })
  // a token spent by a join means a new server: the servers' list (and the navigation) is asked again when the waiting list shrinks
  const waitingIds = (waiting.data ?? []).map((t) => t.id).join('|')
  useEffect(() => { if (isAdmin) void queryClient.invalidateQueries({ queryKey: instancesKey }) }, [waitingIds, isAdmin, queryClient])

  if (!isAdmin) {
    return (
      <ConsoleShell crumbs={[{ label: 'Servers', to: '/instances' }, { label: 'Add a server' }]}>
        <div className="pane"><div className="empty">This screen is for administrators.</div></div>
      </ConsoleShell>
    )
  }

  const onMake = () => {
    setTried(true)
    if (Object.keys(inviteProblems(form)).length === 0) make.mutate()
  }

  return (
    <ConsoleShell crumbs={[{ label: 'Servers', to: '/instances' }, { label: 'Add a server' }]}>
      <div className="pane" data-testid="add-server-page">
        <div className="page-head">
          <h1 className="page-title">Add a server</h1>
          <span className="page-note">A Gateway, a Runner or a Workspace joins this console by a command made here. Nothing is typed into the console afterwards.</span>
        </div>
        <InviteFormView form={form} tried={tried} making={make.isPending} failure={tokenFailure(make.error)} onChange={setForm} onMake={onMake} />
        {make.data && <MadeCommandView made={make.data} now={Date.now()} onCopy={(command) => void navigator.clipboard?.writeText(command)} />}
        <WaitingTokensView tokens={waiting.data} loading={waiting.isLoading} listError={tokenFailure(waiting.error)} busy={cancel.isPending ? cancel.variables ?? null : null}
          failure={tokenFailure(cancel.error)} now={Date.now()} onCancel={(id) => cancel.mutate(id)} />
      </div>
    </ConsoleShell>
  )
}
