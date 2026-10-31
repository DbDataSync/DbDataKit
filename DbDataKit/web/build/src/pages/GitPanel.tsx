import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError, DiffViewer, ErrorBanner } from '@dbdatakit/shell'
import { buildApi } from '../api'
import type { GitChange } from '../types'

/** The files a refused merge names, when the server sent them. */
export function conflictsOf(error: unknown): string[] {
  if (!(error instanceof ApiError)) return []
  const conflicts = (error.body as { conflicts?: unknown } | null | undefined)?.conflicts
  return Array.isArray(conflicts) ? conflicts.filter((c): c is string => typeof c === 'string') : []
}

const languageOf = (path: string) => (path.endsWith('.sql') ? 'sql' : path.endsWith('.yml') || path.endsWith('.yaml') ? 'yaml' : 'plaintext')

/** One changed file: both texts side by side when the server sent them (W11), the unified patch for a binary or very large file. */
export function ChangeDiff({ change }: { change: GitChange }) {
  if (change.sides) {
    return (
      <div data-testid="build-git-diff">
        <DiffViewer before={change.before ?? null} after={change.after ?? null} language={languageOf(change.path)} height={260} testId="build-git-diff-editor" />
      </div>
    )
  }
  return <pre className="mono" style={{ margin: 0, overflow: 'auto', maxHeight: 220 }} data-testid="build-git-patch">{change.patch}</pre>
}

/** After a push: the branch is on the remote; for GitHub, Azure DevOps and GitLab the server sends the address of the page where a pull request is opened (no token, no provider API), and any other remote gets none. */
export function PushedNotice({ branch, pullRequestUrl }: { branch: string; pullRequestUrl: string | null }) {
  return (
    <div className="hint" data-testid="build-git-pushed">
      Pushed {branch}.{' '}
      {pullRequestUrl
        ? <a href={pullRequestUrl} target="_blank" rel="noopener noreferrer" data-testid="build-git-pull-request">Open a pull request</a>
        : <span data-testid="build-git-no-pull-request">Open the pull request where the remote lives.</span>}
    </div>
  )
}

/**
 * Taking a draft into the project (plan 13, W08, D15): what it changes, committing it, and then either merging it (a project the server keeps itself; an approver does it) or pushing its branch (a project that follows a git remote,
 * which is merged where the remote lives).
 */
export function GitPanel({ project, workspace, remote, onMerged }: { project: string; workspace: string; remote: boolean; onMerged: () => void }) {
  const client = useQueryClient()
  const [message, setMessage] = useState('')
  const [expanded, setExpanded] = useState<string | null>(null)
  const key = ['build', project, workspace, 'git']
  const changes = useQuery({ queryKey: [...key, 'changes'], queryFn: () => buildApi.changes(project, workspace) })
  const history = useQuery({ queryKey: [...key, 'history'], queryFn: () => buildApi.history(project, workspace) })
  const refresh = () => client.invalidateQueries({ queryKey: key })

  const commit = useMutation({
    mutationFn: () => buildApi.commit(project, workspace, message.trim()),
    onSuccess: async () => { setMessage(''); await refresh() },
  })
  const push = useMutation({ mutationFn: () => buildApi.push(project, workspace), onSuccess: refresh })
  const merge = useMutation({
    mutationFn: () => buildApi.merge(project, workspace),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['build', project, 'workspaces'] })
      onMerged()
    },
  })

  const list = changes.data?.changes ?? []
  const uncommitted = changes.data?.uncommitted ?? false
  const commits = history.data ?? []
  const conflicts = conflictsOf(merge.error)

  return (
    <div className="card" style={{ marginTop: 14 }} data-testid="build-git">
      <div className="hint" style={{ marginBottom: 6 }}>Changes in this workspace</div>
      {list.length === 0 && <div className="empty" data-testid="build-git-none">Nothing differs from where the workspace started.</div>}
      {list.map((c) => (
        <div key={c.path}>
          <button type="button" className="grid-row mono" style={{ width: '100%', textAlign: 'left' }} onClick={() => setExpanded(expanded === c.path ? null : c.path)} data-testid="build-git-change">
            {c.status} {c.path}
          </button>
          {expanded === c.path && <ChangeDiff change={c} />}
        </div>
      ))}
      <div className="row" style={{ gap: 12, margin: '10px 0', flexWrap: 'wrap' }}>
        <input value={message} onChange={(e) => setMessage(e.target.value)} placeholder="what this change is" aria-label="Commit message" data-testid="build-git-message" style={{ flex: 1, minWidth: 220 }} />
        <button type="button" className="btn" disabled={message.trim() === '' || !uncommitted || commit.isPending} onClick={() => commit.mutate()} data-testid="build-git-commit">Commit</button>
        {remote
          ? <button type="button" className="btn btn-primary" disabled={commits.length === 0 || push.isPending} onClick={() => push.mutate()} data-testid="build-git-push">Push the branch</button>
          : <button type="button" className="btn btn-primary" disabled={commits.length === 0 || uncommitted || merge.isPending} onClick={() => merge.mutate()} data-testid="build-git-merge">Merge into the project</button>}
      </div>
      {!remote && uncommitted && commits.length > 0 && <div className="hint" data-testid="build-git-uncommitted">There are changes no commit has taken; commit them before merging.</div>}
      {remote && <div className="hint">This project follows a git remote: push the branch and merge it where the remote lives.</div>}
      {push.isSuccess && <PushedNotice branch={push.data.branch} pullRequestUrl={push.data.pull_request_url ?? null} />}
      {conflicts.length > 0 && (
        <div className="empty" data-testid="build-git-conflicts">Nothing was merged. These files conflict with the project: {conflicts.join(', ')}</div>
      )}
      <ErrorBanner error={commit.error ?? push.error ?? merge.error ?? changes.error ?? history.error} />
      <div className="hint" style={{ margin: '10px 0 4px' }}>Commits</div>
      {commits.length === 0 && <div className="empty">No commits in this workspace yet.</div>}
      {commits.map((c) => (
        <div key={c.sha} className="grid-row" style={{ gap: 14 }} data-testid="build-git-commit-row">
          <span className="mono">{c.sha.slice(0, 8)}</span><span>{c.message}</span><span className="hint">{c.author}</span>
        </div>
      ))}
    </div>
  )
}
