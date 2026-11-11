import { useParams } from 'react-router-dom'
import { DocsViewer, useAbout } from '@dbdatakit/shell'
import { useAdminHost } from '../host'

/**
 * The module's own docs inside the module's frame (plan 13a, U11): `/docs` is the index, `/docs/:page` one page. What is shown, and how, is the shell's generic viewer over the `DocsSource` the product gave
 * the host; this is only the frame around it and the version it describes (the server's, as it reports it).
 */
export function DocsPage() {
  const { Frame, docs } = useAdminHost()
  const { page } = useParams()
  const { data: about } = useAbout()

  return (
    <Frame crumbs={[{ label: 'Docs', to: page ? '/docs' : undefined }]}>
      {docs
        ? <DocsViewer source={docs} page={page} base="/docs" versionNote={about?.version ? `For the version running here: ${about.version}` : 'For the version running here'} />
        : <div className="pane"><div className="empty">This product has no docs in this build.</div></div>}
    </Frame>
  )
}
