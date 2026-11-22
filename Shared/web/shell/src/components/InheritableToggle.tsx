/**
 * A setting that either takes a broader level's answer or overrides it: inherit (null) or a value of this level's own. Null is not "off": a level
 * never asked and a level that said no are different settings, and folding them would stop a broader default from ever reaching it.
 */
export function InheritableToggle({ label, description, value, inherited, onChange, testId, from = 'the replication' }: {
  label: string
  description: string
  /** Null means inherit. */
  value: boolean | null
  /** What the broader level resolves to, shown while inheriting. */
  inherited: boolean
  onChange: (next: boolean | null) => void
  testId: string
  /** The broader level in words, after "from". */
  from?: string
}) {
  const overriding = value !== null
  const effective = value ?? inherited

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }} data-testid={testId}>
      <div className="row" style={{ gap: 8 }}>
        <span className="card-title sm">{label}</span>
        {!overriding && <span className="badge">INHERITED</span>}
        <span className="spacer row" style={{ gap: 7 }}>
          <button
            type="button"
            className={`toggle ${overriding ? 'on' : ''}`}
            // turning the override on starts from what is in effect, a starting point rather than a reset; off goes back to inheriting
            onClick={() => onChange(overriding ? null : effective)}
            aria-pressed={overriding}
            data-testid={`${testId}-override`}
          />
          <span style={{ font: '500 11.5px var(--ui)', color: 'var(--ink-4)' }}>Override here</span>
        </span>
      </div>

      <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <input
          type="checkbox"
          checked={effective}
          disabled={!overriding}
          onChange={(e) => onChange(e.target.checked)}
          data-testid={`${testId}-checkbox`}
        />
        <span style={{ color: overriding ? 'var(--ink)' : 'var(--ink-8)' }}>
          {effective ? 'On' : 'Off'}
          {!overriding && ` — from ${from}`}
        </span>
      </label>

      <span className="hint">{description}</span>
    </div>
  )
}
