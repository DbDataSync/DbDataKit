/** What a publish to a runner left there: the version, whether it is staged or served, how many files went and how many the runner had, and the tasks it carries. Shared by Build's Publish tab and the Runner's publish from files (plan 33, UE05, UE10). */
export interface StagedVersionData {
  package: string
  channel: string
  /** `stage` or `serve`. */
  mode: string
  version: number
  /** False when the content is the same as the version the channel already had. */
  changed: boolean
  /** Files sent; absent for a promotion, which sends none. */
  uploaded?: number
  /** Files the runner already had. */
  skipped?: number
  /** Set when the version was promoted from another channel. */
  promotedFrom?: string
  tasks: { name: string; detail?: string }[]
  /** Tasks the publishing product still schedules until they are cut over (Build). */
  oldScheduled?: string[]
  /** Slots bound by the same call. */
  bound?: string[]
}

/** What the card says about the state of the version: staged versions wait for every runner of the group to have them ready. */
export function stagedText(data: Pick<StagedVersionData, 'package' | 'channel' | 'mode' | 'version' | 'changed' | 'promotedFrom'>): string {
  if (data.promotedFrom) return `Version ${data.version} of ${data.package} is served on ${data.channel} (promoted from ${data.promotedFrom}).`
  const base = data.mode === 'stage'
    ? `Version ${data.version} of ${data.package} is staged on ${data.channel}: the runners prepare it and it activates once every runner of the group has it ready (the Runner screens show it).`
    : `Version ${data.version} of ${data.package} is served on ${data.channel}.`
  return data.changed ? base : `${base} It is the same content as before; nothing changed.`
}

/** The card of a published version. `testIdPrefix` names its test ids: `<prefix>-done`, `-summary`, `-counts`, `-tasks`, `-task`, `-old`, `-bound`. */
export function StagedVersionView({ data, testIdPrefix }: { data: StagedVersionData; testIdPrefix: string }) {
  const id = (name: string) => `${testIdPrefix}-${name}`
  return (
    <div className="card" data-testid={id('done')}>
      <div className="card-body">
        <div data-testid={id('summary')}>{stagedText(data)}</div>
        {!data.promotedFrom && data.uploaded !== undefined && <div className="hint" data-testid={id('counts')}>{data.uploaded} file(s) sent, {data.skipped ?? 0} already on the runner.</div>}
        {data.tasks.length > 0 && (
          <div style={{ marginTop: 8 }} data-testid={id('tasks')}>
            {data.tasks.map((t) => <div key={t.name} data-testid={id('task')} data-task={t.name}><span className="mono">{t.name}</span>{t.detail ? <> <span className="hint">{t.detail}</span></> : null}</div>)}
          </div>
        )}
        {(data.oldScheduled?.length ?? 0) > 0 && <div className="hint" style={{ marginTop: 8 }} data-testid={id('old')}>Still scheduled by this server until cut over: {data.oldScheduled!.join(', ')}.</div>}
        {(data.bound?.length ?? 0) > 0 && <div className="hint" data-testid={id('bound')}>Bound in {data.channel}: {data.bound!.join(', ')}.</div>}
      </div>
    </div>
  )
}
