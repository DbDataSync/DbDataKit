/** The kinds a publisher can send to a gateway, each with a trust store of its own (plan 30 PB01: the serving kinds and the views'). */
export const TRUST_KINDS = ['semantic-model', 'data-source', 'entity-model', 'app', 'app-instance', 'app-nav', 'app-approval', 'dashboard', 'report'] as const

/** What the mode of the group means for signatures. */
export function groupModeText(testGroup: boolean): string {
  return testGroup
    ? 'Signatures are optional here unless an administrator requires them for a kind: use it for a group where people try things out. Signed versions are still checked.'
    : 'Signatures are required for every kind unless an administrator made them optional for it: an unsigned version is refused and the version that serves stays.'
}
