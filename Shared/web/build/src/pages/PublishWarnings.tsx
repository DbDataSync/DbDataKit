/**
 * What the Gateway said about a publish that went through, one line each (plan 22a GP02/GP12/GP13, owner 2026-11-12: row rules limit reads only, "with app visible and documented warning"):
 * a mask that hides the column instead, a second model that reads a table another model puts rules on, row rules that do not limit writes. Plain text, never an error: the publish happened.
 */
export function PublishWarnings({ warnings, testId = 'publish-warning' }: { warnings?: (string | null)[] | null; testId?: string }) {
  const lines = (warnings ?? []).filter((w): w is string => typeof w === 'string' && w.trim() !== '')
  if (lines.length === 0) return null
  return (
    <div className="banner warn" role="status" style={{ marginTop: 8, flexDirection: 'column', alignItems: 'flex-start' }} data-testid={`${testId}s`}>
      <strong>The Gateway published it with {lines.length === 1 ? 'a warning' : `${lines.length} warnings`}:</strong>
      <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>
        {lines.map((w, i) => <li key={i} data-testid={testId}>{w}</li>)}
      </ul>
    </div>
  )
}
