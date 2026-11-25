import { BindingTest, CATALOG_CACHE_FILE, ChannelPicker, QueryDescribe, useChannelChoice, useDraft, useDraftFile, type DescribedColumn } from '@dbdatakit/workspace'

/** A channel choice for a connection of the draft, offered first where the draft's catalog cache says the connection was read (unsaved edits included). */
function useDraftChannel(connection: string) {
  const draft = useDraft()
  const cache = useDraftFile(CATALOG_CACHE_FILE)
  const choice = useChannelChoice(draft.project, connection, cache.text)
  const bound = choice.channels.data?.channels.some((c) => c.channel === choice.channel && c.bound) ?? false
  return { draft, choice, bound }
}

/** What a panel says when the screens are not hosted by a console, which is the only way they read a database. */
const unhosted = (testId: string) => (
  <span className="hint" data-testid={`${testId}-unavailable`}>A test or a description runs on a Runner and is asked through the console: open this draft from the console's Workspace screens.</span>
)

/**
 * **Test** of an endpoint's connection: the channel picker (which offers **Set up for development** where a channel other than production does not bind
 * the connection's slot), then the Runner's binding test in the channel picked, asked through the console as the signed-in person. The steps are shown
 * with the Runner's reason; no row comes back. The draft needs no build: the test reads the binding, not a version.
 * Design: docs/authoring-metadata.md#testing-a-connection
 */
export function ConnectionTestPanel({ connection, onClose, testId }: { connection: string; onClose: () => void; testId: string }) {
  const { draft, choice, bound } = useDraftChannel(connection)
  return (
    <div className="stack" style={{ gap: 6, padding: '8px 0' }} data-testid={testId} data-connection={connection}>
      {choice.workspace === null ? unhosted(testId) : (
        <>
          <ChannelPicker {...choice.picker} canEdit={draft.canEdit} />
          {bound
            ? <BindingTest project={draft.project} connection={connection} runner={choice.runner} channel={choice.channel} />
            : <span className="hint" data-testid={`${testId}-pick`}>Pick a channel that binds the slot of {connection} to test it there.</span>}
        </>
      )}
      <div><button type="button" className="btn-link quiet" style={{ padding: 0 }} onClick={onClose} data-testid={`${testId}-close`}>Close</button></div>
    </div>
  )
}

/**
 * **Describe columns** of a mapping's source query: the channel picker, then the Runner's built-in `describe` of the query in the channel picked, with the
 * login that channel binds. The query is described, never run. **Use these columns** hands the columns to `onColumns`, which writes them into the mapping.
 * Design: docs/authoring-metadata.md#a-sync-project
 */
export function QueryDescribePanel({ connection, sql, onColumns, testId }: { connection: string; sql: string; onColumns: (columns: DescribedColumn[]) => void; testId: string }) {
  const { draft, choice, bound } = useDraftChannel(connection)
  if (choice.workspace === null) return unhosted(testId)
  return (
    <div className="stack" style={{ gap: 6 }} data-testid={testId} data-connection={connection}>
      <ChannelPicker {...choice.picker} canEdit={draft.canEdit} />
      {bound
        ? <QueryDescribe project={draft.project} connection={connection} runner={choice.runner} channel={choice.channel} sql={sql} onColumns={onColumns} canEdit={draft.canEdit} />
        : <span className="hint" data-testid={`${testId}-pick`}>Pick a channel that binds the slot of {connection} to describe the query there.</span>}
    </div>
  )
}
