import { useEffect, useRef, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Link, StagedVersionView } from '@dbdatakit/shell'
import { runnerApi } from '../api'
import {
  emptyPublishForm, hashFiles, manifestIn, packageFiles, publishBlock, readManifest, sendPackage, sizeText, stepText,
  type FileLike, type HashedFile, type ManifestRead, type PackageFile, type Prepared, type PublishApi, type PublishForm, type Published, type Step,
} from '../publishFiles'

/** How many paths the file list shows before it says how many more there are. */
const SHOWN = 12

export interface PublishFromFilesViewProps {
  /** Whether the person is offered Publish (the admin role; the runner checks again). */
  canPublish: boolean
  /** The folder chosen: how many files, how big, and whether they are still being read. Null before one is chosen. */
  folder: { count: number; bytes: number; hashing: boolean; paths: string[] } | null
  manifest: ManifestRead | null
  /** Where the manifest came from: the folder's `package.yaml` or a file chosen on its own. */
  manifestFrom: 'folder' | 'file' | null
  prepared: Prepared | null
  form: PublishForm
  busy: boolean
  step: Step | null
  /** The words of the last refusal or failure. */
  failure: string | null
  result: Published | null
  onFolder: (list: FileList | null) => void
  onManifest: (file: File | null) => void
  onForm: (form: PublishForm) => void
  onPublish: () => void
}

/** Publishing a package from files without its calls: a folder and a manifest chosen in the browser, the files it holds, the channel, staged or served, and what the runner left there. Hook-free so a test can draw it and press its button. */
export function PublishFromFilesView(p: PublishFromFilesViewProps) {
  const block = publishBlock(p.canPublish, p.form, p.folder === null ? null : { count: p.folder.count, hashing: p.folder.hashing }, p.manifest, p.prepared)
  const set = (change: Partial<PublishForm>) => p.onForm({ ...p.form, ...change })
  return (
    <div className="card" data-testid="runner-pub">
      <div className="card-body">
        <div className="card-title">Publish a package from files</div>
        <div className="hint" style={{ marginBottom: 10 }} data-testid="runner-pub-about">
          Choose the folder of a package (its <span className="mono">package.yaml</span> and the files it launches) as <span className="mono">dbdatarunner package publish</span> would send it. Files the runner already has are not sent again. The version is{' '}
          <strong>staged</strong>: the runners prepare it and an administrator activates it below; or serve it at once.
        </div>
        <div className="row" style={{ gap: 12, flexWrap: 'wrap', marginBottom: 10 }}>
          <label className="row" style={{ gap: 8 }}>
            <span className="hint">Folder</span>
            <input type="file" multiple {...{ webkitdirectory: '' }} disabled={!p.canPublish || p.busy} onChange={(e) => p.onFolder(e.target.files)} data-testid="runner-pub-folder" />
          </label>
          <label className="row" style={{ gap: 8 }}>
            <span className="hint">Manifest, if it is not in the folder</span>
            <input type="file" accept=".yaml,.yml" disabled={!p.canPublish || p.busy} onChange={(e) => p.onManifest(e.target.files?.[0] ?? null)} data-testid="runner-pub-manifest" />
          </label>
        </div>
        {p.folder !== null && (
          <div data-testid="runner-pub-files" data-count={p.folder.count}>
            <div className="hint" data-testid="runner-pub-folder-summary">
              {p.folder.count === 0 ? 'The folder has no file.' : `${p.folder.count} file(s), ${sizeText(p.folder.bytes)}${p.folder.hashing ? ', being read...' : ''}`}
            </div>
            <div className="mono" style={{ fontSize: 12, margin: '4px 0 8px' }}>
              {p.folder.paths.slice(0, SHOWN).map((path) => <div key={path} data-testid="runner-pub-file">{path}</div>)}
              {p.folder.paths.length > SHOWN && <div className="hint">and {p.folder.paths.length - SHOWN} more</div>}
            </div>
          </div>
        )}
        {p.manifest !== null && (
          p.manifest.ok
            ? <div data-testid="runner-pub-manifest-read" data-package={p.manifest.name}>Package <strong className="mono">{p.manifest.name}</strong> from the {p.manifestFrom === 'file' ? 'manifest chosen' : 'package.yaml in the folder'}.</div>
            : <div className="field-error" role="alert" data-testid="runner-pub-manifest-problem">{p.manifest.problem}</div>
        )}
        <div className="row" style={{ gap: 12, marginTop: 10, flexWrap: 'wrap' }}>
          <label className="row" style={{ gap: 8 }}>
            <span className="hint">Channel</span>
            <input className="input mono" value={p.form.channel} placeholder="for example prod" disabled={!p.canPublish} onChange={(e) => set({ channel: e.target.value })} data-testid="runner-pub-channel" style={{ width: 140 }} />
          </label>
          <label className="row" style={{ gap: 6 }}>
            <input type="checkbox" checked={p.form.serve} disabled={!p.canPublish} onChange={(e) => set({ serve: e.target.checked })} data-testid="runner-pub-serve" />
            <span className="hint">Serve it at once instead of staging it</span>
          </label>
          <label className="row" style={{ gap: 8 }}>
            <span className="hint">Note</span>
            <input className="input" value={p.form.note} placeholder="optional" disabled={!p.canPublish} onChange={(e) => set({ note: e.target.value })} data-testid="runner-pub-note" style={{ width: 220 }} />
          </label>
        </div>
        <div className="row" style={{ gap: 12, marginTop: 10 }}>
          <button type="button" className="btn btn-primary" disabled={block !== null || p.busy} onClick={p.onPublish} data-testid="runner-pub-button">{p.busy ? 'Publishing...' : 'Publish'}</button>
          {block !== null && !p.busy && <span className="hint" data-testid="runner-pub-why">{block}</span>}
          {p.busy && p.step && <span className="hint" role="status" data-testid="runner-pub-step">{stepText(p.step)}</span>}
        </div>
        {p.failure && <div className="banner error" role="alert" data-testid="runner-pub-failed" style={{ marginTop: 10 }}><span className="mark">!</span><span>{p.failure}</span></div>}
        {p.result && (
          <div style={{ marginTop: 10 }}>
            <StagedVersionView
              testIdPrefix="runner-pub"
              data={{ package: p.result.name, channel: p.result.channel, mode: p.result.mode, version: p.result.version, changed: p.result.changed, uploaded: p.result.uploaded, skipped: p.result.skipped, tasks: p.result.tasks.map((name) => ({ name })) }}
            />
            <div className="hint" style={{ marginTop: 6 }}><Link to={`/packages/${encodeURIComponent(p.result.name)}`} data-testid="runner-pub-open">Open {p.result.name}</Link>{p.result.signature ? ` (recorded as ${p.result.signature})` : ''}</div>
          </div>
        )}
      </div>
    </div>
  )
}

const api: PublishApi = {
  has: runnerApi.hasFile,
  put: (sha256, file) => runnerApi.putFile(sha256, file as unknown as Blob),
  publish: (name, body) => runnerApi.publishPackage(name, body),
}

/** Publishing a package from a folder of files (plan 33, UE10): read and hashed in the browser, the missing files sent one at a time, then the version published; the runner's refusal is shown at the step it came at. */
export function PublishFromFiles({ canPublish }: { canPublish: boolean }) {
  const client = useQueryClient()
  const [files, setFiles] = useState<PackageFile[] | null>(null)
  const [manifestFile, setManifestFile] = useState<FileLike | null>(null)
  const [manifestText, setManifestText] = useState<string | null>(null)
  const [hashed, setHashed] = useState<HashedFile[] | null>(null)
  const [form, setForm] = useState<PublishForm>(emptyPublishForm)
  const [step, setStep] = useState<Step | null>(null)
  const [failure, setFailure] = useState<string | null>(null)
  const [result, setResult] = useState<Published | null>(null)
  const generation = useRef(0)

  const inFolder = files ? manifestIn(files) : undefined
  const source = manifestFile ?? inFolder?.file ?? null

  // the files are read and hashed once per choice; a later choice makes an earlier read stale
  useEffect(() => {
    const mine = ++generation.current
    setHashed(null)
    if (!files || files.length === 0) return
    void hashFiles(files).then((done) => { if (generation.current === mine) setHashed(done) })
  }, [files])
  useEffect(() => {
    let current = true
    setManifestText(null)
    void source?.text().then((text) => { if (current) setManifestText(text) })
    return () => { current = false }
  }, [source])

  const manifest: ManifestRead | null = source === null ? null : manifestText === null ? null : readManifest(manifestText)
  const prepared: Prepared | null = manifest?.ok && hashed && manifestText !== null
    ? { manifest: manifestText, name: manifest.name, files: manifestFile ? hashed : hashed.filter((f) => f.path !== inFolder?.path) }
    : null

  const publish = useMutation({
    mutationFn: () => sendPackage(api, prepared!, form, setStep),
    onMutate: () => { setFailure(null); setResult(null) },
    onSuccess: (published) => { setResult(published); setStep(null); void client.invalidateQueries({ queryKey: ['runner'] }) },
    onError: (error) => { setStep(null); setFailure(error instanceof Error ? error.message : 'The publish failed.') },
  })

  const list = prepared?.files ?? files ?? []
  return (
    <PublishFromFilesView
      canPublish={canPublish}
      folder={files === null ? null : { count: list.length, bytes: list.reduce((n, f) => n + f.file.size, 0), hashing: files.length > 0 && hashed === null, paths: list.map((f) => f.path) }}
      manifest={source !== null && manifestText === null ? null : manifest}
      manifestFrom={manifestFile ? 'file' : inFolder ? 'folder' : null}
      prepared={prepared} form={form} busy={publish.isPending} step={step} failure={failure} result={result}
      onFolder={(chosen) => { setFailure(null); setResult(null); setFiles(chosen && chosen.length > 0 ? packageFiles([...chosen]) : null) }}
      onManifest={(chosen) => { setFailure(null); setResult(null); setManifestFile(chosen) }}
      onForm={(next) => { setForm(next); setResult(null) }}
      onPublish={() => publish.mutate()}
    />
  )
}
