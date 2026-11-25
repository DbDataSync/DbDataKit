import { useState } from 'react'
import { Link as ConsoleLink } from 'react-router-dom'
import { ErrorBanner } from '@dbdatakit/shell'
import {
  findingsSummary, PREFLIGHT_TASK, PreflightFindings, preflightPartsOf, useNewestRun, usePublishedChannels, type PreflightPart, type RunnerVersion,
} from '@dbdatakit/workspace'

/**
 * The parts of a set's preflight that are about one replication, or about one mapping of it. The set's preflight names its parts `<replication>/<mapping>`
 * for a mapping's source and target, `<replication> tracking` for where the replication keeps its state, and `<replication>` for a replication it could
 * not read at all.
 */
export function partsOf(parts: readonly PreflightPart[], replication: string, mapping?: string): PreflightPart[] {
  if (mapping !== undefined) return parts.filter((p) => p.part === `${replication}/${mapping}`)
  return parts.filter((p) => p.part === replication || p.part === `${replication} tracking` || p.part.startsWith(`${replication}/`))
}

/** What a part is called on the screen: its mapping, where the replication keeps its state, or the replication itself. */
export function partLabel(part: string, replication: string): string {
  if (part.startsWith(`${replication}/`)) return part.slice(replication.length + 1)
  if (part === `${replication} tracking`) return 'the replication\'s state on its target'
  return part
}

const channelKey = (v: RunnerVersion) => `${v.target}\u0000${v.channel}`

/**
 * The newest `preflight` run of the set in a channel this draft was published to, on a replication's or a mapping's Provisioning sub-tab: the channel
 * picked from those of the draft's deploys (the newest one first), the run's version, outcome and time, its link on the Runner, and what it found for
 * each mapping of the replication (or for this mapping). The run is read through the console as the signed-in person; nothing of it is committed, since
 * a check describes the logins of a channel at a moment, not the draft. A preflight runs when a version is served in production, and on **Re-check** on
 * the Runs tab.
 * Design: docs/authoring-metadata.md#a-sync-project
 */
export function NewestPreflight({ project, id, replication, mapping }: { project: string; id: string; replication: string; mapping?: string }) {
  const { server, deploys, channels } = usePublishedChannels(project, id, PREFLIGHT_TASK)
  const [picked, setPicked] = useState('')
  const version = channels.find((v) => channelKey(v) === picked) ?? channels[0] ?? null
  const newest = useNewestRun(version)
  if (server === null) return null
  const run = newest.data?.run ?? null
  const all = run ? preflightPartsOf(run) : null
  const parts = all ? partsOf(all, replication, mapping) : null
  const findings = parts?.flatMap((p) => p.findings) ?? []
  const errors = findings.filter((f) => f.severity === 'error').length
  const what = mapping === undefined ? 'this replication' : 'this mapping'
  return (
    <div className="card" data-testid="newest-preflight" data-channel={version?.channel ?? ''} data-outcome={run?.outcome ?? 'none'} data-errors={errors}>
      <div className="card-head">
        <span className="card-title">Newest preflight</span>
        <span className="card-note">what the set's preflight found for {what} on a Runner, with the logins the channel binds</span>
      </div>
      <div className="card-body" style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <ErrorBanner error={deploys.error ?? newest.error} />
        {deploys.isSuccess && channels.length === 0 && (
          <span className="hint" data-testid="newest-preflight-unpublished">No channel of a Runner has a version of this draft with a preflight yet: build it and publish it on the Builds tab.</span>
        )}
        {channels.length > 0 && (
          <div className="row" style={{ gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <label className="hint" htmlFor="newest-preflight-channel">Channel</label>
            <select id="newest-preflight-channel" className="select" value={version ? channelKey(version) : ''} onChange={(e) => setPicked(e.target.value)} data-testid="newest-preflight-channel">
              {channels.map((v) => <option key={channelKey(v)} value={channelKey(v)}>{v.channel} on {v.targetName}</option>)}
            </select>
          </div>
        )}
        {version && newest.isSuccess && !run && (
          <span className="hint" data-testid="newest-preflight-none">No preflight has run in {version.channel} on {version.targetName} yet. It runs when a version is served in production, and on Re-check on the Runs tab.</span>
        )}
        {version && run && (
          <>
            <div className="hint" data-testid="newest-preflight-when">
              preflight of version {run.version ?? version.version}, {run.outcome}, {run.finished ? `finished ${run.finished}` : `started ${run.started}`}
              {newest.data?.link && <> <ConsoleLink to={newest.data.link} data-testid="newest-preflight-link">The run on {version.targetName}</ConsoleLink></>}
            </div>
            {run.failure && <div className="error" data-testid="newest-preflight-failure">{run.failure.message}</div>}
            {parts && parts.length === 0 && <span className="hint" data-testid="newest-preflight-not-checked">The run did not check {what}: it was not enabled, or not in that version.</span>}
            {parts && parts.length > 0 && <div className={errors > 0 ? 'error' : 'hint'} data-testid="newest-preflight-summary">{findingsSummary(findings)}</div>}
            {parts?.map((p) => (
              <div key={p.part} data-testid="newest-preflight-part" data-part={p.part} data-findings={p.findings.length}>
                <strong className="mono">{partLabel(p.part, replication)}</strong>
                {p.findings.length === 0
                  ? <span className="hint"> found nothing in the way.</span>
                  : <PreflightFindings findings={p.findings} />}
              </div>
            ))}
          </>
        )}
      </div>
    </div>
  )
}
