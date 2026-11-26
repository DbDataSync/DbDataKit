import { CATALOG_CACHE_FILE, CatalogRefresh, useDraft, useDraftFile } from '@dbdatakit/workspace'
import { useConnectionReader } from './connectionReader'

/**
 * **Refresh** of a connection, or of one table of it, from a Runner channel, where a Designer screen offers it: the shared channel picker and read of
 * `@dbdatakit/workspace`, asked through the console, never through the Workspace server. The difference is made in the browser against the cache the
 * draft holds, unsaved edits included, and shown before anything is replaced; **Accept** puts the new cache in the draft as an unsaved edit of
 * `catalog-cache.yaml`, the same one Files shows, and closes the panel. Nothing is read while the draft's cache is still loading or could not be
 * read, so an accepted read never replaces a cache the screen has not seen. A connection whose driver is Sync's own is read by the set's own package
 * (`useConnectionReader`), which needs a build in the channel; where there is none the panel offers **Build and stage**.
 * Design: docs/authoring-metadata.md#the-catalog-cache
 */
export function RefreshPanel({ connection, table, onClose, testId }: {
  connection: string
  /** One table of the connection; the whole connection when absent. */
  table?: { schema: string; name: string }
  onClose: () => void
  testId: string
}) {
  const draft = useDraft()
  const file = useDraftFile(CATALOG_CACHE_FILE)
  const reading = useConnectionReader(connection)
  return (
    <div className="stack" style={{ gap: 6, padding: '8px 0' }} data-testid={testId} data-connection={connection}>
      {file.loading || draft.filesLoading
        ? <span className="hint">Reading the draft's catalog cache…</span>
        : file.error ? <span className="hint" data-testid={`${testId}-unreadable`}>{CATALOG_CACHE_FILE} could not be read from the draft, so a refresh would replace what it holds unseen. Open it in Files.</span>
        : (
          <>
          {reading.why && <span className="hint" data-testid={`${testId}-own-package`}>{reading.why}</span>}
          <CatalogRefresh
            project={draft.project}
            connection={connection}
            table={table}
            current={file.text}
            canEdit={draft.canEdit}
            onAccept={(text) => { draft.edit(CATALOG_CACHE_FILE, text); onClose() }}
            reader={reading.reader}
          />
          </>
        )}
      <div><button type="button" className="btn-link quiet" style={{ padding: 0 }} onClick={onClose} data-testid={`${testId}-close`}>Close</button></div>
    </div>
  )
}
