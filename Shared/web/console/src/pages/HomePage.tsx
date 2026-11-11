import { ConsoleShell } from '../ConsoleShell'

/** The console's first screen. The servers it manages arrive with the instances page; until then it says so rather than showing an empty list that looks like a failure. */
export function HomePage() {
  return (
    <ConsoleShell crumbs={[{ label: 'Home', heading: true }]}>
      <div className="pane" data-testid="home-page">
        <div className="page-head">
          <h1 className="page-title">dbdatakit</h1>
          <span className="page-note">The console for your DbData servers.</span>
        </div>
        <div className="empty" data-testid="home-no-servers">No server is paired with this console yet.</div>
      </div>
    </ConsoleShell>
  )
}
