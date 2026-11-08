/** The three states a widget is in besides drawn: waiting for its rows, with none, and failed. Each says so in words and to a screen reader. */
export function LoadingTile({ what }: { what?: string }) {
  return <div className="vz-state vz-loading" role="status" aria-busy="true" aria-live="polite" data-testid="vz-loading">{what ? `Loading ${what}…` : 'Loading…'}</div>
}

export function EmptyTile({ message = 'No data.' }: { message?: string }) {
  return <div className="vz-state vz-empty" role="note" data-testid="vz-empty">{message}</div>
}

export function ErrorTile({ code, message, path, suggestions }: { code: string; message: string; path?: string | null; suggestions?: string[] | null }) {
  return (
    <div className="vz-state vz-error" role="alert" data-testid="vz-widget-error">
      <code>{code}</code>
      {path ? <span className="vz-path">{` ${path}`}</span> : null}
      {`: ${message}`}
      {suggestions?.length ? ` Did you mean ${suggestions.join(', ')}?` : ''}
    </div>
  )
}
