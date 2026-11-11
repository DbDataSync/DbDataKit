import { ViewsClientError, catalogFromModel, validateText } from '@dbdatakit/visuals'
import type { Catalog, DocumentSummary, OpenedDocument, SaveResult, ViewKind, ViewsClient } from '@dbdatakit/visuals'
import type { CommandAnswer, ProjectFile } from '../types'

/** The folders of a Build project that hold dashboards and reports (plan 32 DBR29), . */
export const FOLDERS: Record<ViewKind, string> = { dashboard: 'dashboards', report: 'reports' }

/** The kind of a project path (`dashboards/x.yaml` is a dashboard), the name (`x`) and the path, or null for a file that is not a document of the project. */
export function viewFileOf(path: string): { kind: ViewKind; name: string; path: string } | null {
  const m = /^(dashboards|reports)\/([^/]+)\.(yaml|yml|json)$/.exec(path)
  return m ? { kind: m[1] === 'reports' ? 'report' : 'dashboard', name: m[2]!, path } : null
}

/** The document files among a list of project files, dashboards first, each in name order. */
export function viewFiles(files: ProjectFile[]): { kind: ViewKind; name: string; path: string; hash: string }[] {
  const found = files.flatMap((f) => { const v = viewFileOf(f.path); return v ? [{ ...v, hash: f.hash }] : [] })
  return found.sort((a, b) => (a.kind === b.kind ? a.name.localeCompare(b.name) : a.kind === 'dashboard' ? -1 : 1))
}

/** The name a semantic file gives its model (`name:` at the top of the file), or null. */
export function semanticModelName(text: string): string | null {
  const m = /^name:\s*['"]?([A-Za-z0-9_.-]+)['"]?\s*(#.*)?$/m.exec(text)
  return m ? m[1]! : null
}

/** What a file call of a workspace refused with: the server's code (DDB-471 is a stale hash or a file that exists already) and its words. */
export class FileRefusal extends Error {
  readonly code: string
  constructor(code: string, message: string) {
    super(message)
    this.name = 'FileRefusal'
    this.code = code
  }
}

/** The calls the store needs of a workspace's files; Build's server answers them (`buildFilesApi`), a test answers them in memory. */
export interface FilesApi {
  list(): Promise<ProjectFile[]>
  /** The file's text and hash, or null when there is no such file. */
  read(path: string): Promise<{ content: string; hash: string } | null>
  create(path: string, content: string): Promise<{ hash: string }>
  write(path: string, content: string, hash: string): Promise<{ hash: string }>
  remove(path: string, hash: string): Promise<void>
}

/** What the server said when a command did not do its work, in its own words. */
function refusalOf(answer: CommandAnswer<unknown>): FileRefusal {
  const document = answer.document
  const diagnostic = document?.diagnostics?.[0]
  return new FileRefusal(diagnostic?.code ?? 'DDB-000', document?.errors?.[0] ?? diagnostic?.found ?? answer.text ?? 'The server did not do it.')
}

/** The calls of `FilesApi` over Build's command route for one workspace. */
export function buildFilesApi(api: {
  files: () => Promise<CommandAnswer<{ files: ProjectFile[] }>>
  read: (path: string) => Promise<CommandAnswer<{ content: string; hash: string }>>
  create: (path: string, content: string) => Promise<CommandAnswer<{ hash: string }>>
  write: (path: string, content: string, hash: string) => Promise<CommandAnswer<{ hash: string }>>
  remove: (path: string, hash: string) => Promise<CommandAnswer<unknown>>
}): FilesApi {
  return {
    async list() {
      const answer = await api.files()
      if (!answer.document?.ok) throw refusalOf(answer)
      return answer.document.data.files
    },
    async read(path) {
      const answer = await api.read(path)
      if (!answer.document?.ok) return null
      return { content: answer.document.data.content, hash: answer.document.data.hash }
    },
    async create(path, content) {
      const answer = await api.create(path, content)
      if (!answer.document?.ok) throw refusalOf(answer)
      return { hash: answer.document.data.hash }
    },
    async write(path, content, hash) {
      const answer = await api.write(path, content, hash)
      if (!answer.document?.ok) throw refusalOf(answer)
      return { hash: answer.document.data.hash }
    },
    async remove(path, hash) {
      const answer = await api.remove(path, hash)
      if (!answer.document?.ok) throw refusalOf(answer)
    },
  }
}

export interface WorkspaceViewsOptions {
  /** The catalog of a model of the workspace (the wells' fields); absent: the wells take typed names. */
  catalog?: (model: string) => Promise<unknown>
  /** What draws rows: a Gateway's views module through the console's relay. Absent: no rows, and the preview says why. */
  rows?: ViewsClient | null
}

const NO_ROWS = 'There are no rows to draw: choose a Gateway that runs the views module as the preview source. Saving and publishing do not need one.'

/**
 * A `ViewsClient` over the dashboards and reports of a Build workspace (plan 32 DBR29): the files of its `dashboards/` and `reports/` folders, read and written through Build's file commands with the file's hash as the version, so a stale
 * save is a `version_conflict` naming the stored hash, a new name that exists already is one too, and saving needs the author role of the server (it refuses the write otherwise). The text is checked in the browser as the loaders check it; what
 * needs the project's models (a field the model lacks) is the project check's, shown by the Views tab after each save. Rows come from `rows`, a Gateway's module, or are refused with a sentence that says how to get them.
 */
export function workspaceViewsClient(files: FilesApi, options: WorkspaceViewsOptions = {}): ViewsClient {
  const find = async (kind: ViewKind, name: string) => {
    const all = viewFiles(await files.list())
    return all.find((f) => f.kind === kind && f.name === name)
  }
  const pathOf = (kind: ViewKind, name: string) => `${FOLDERS[kind]}/${name}.yaml`
  const wrap = async <T,>(work: () => Promise<T>): Promise<T> => {
    try { return await work() } catch (e) {
      if (e instanceof FileRefusal) throw new ViewsClientError(e.code === 'DDB-470' ? 'invalid' : 'unavailable', e.message)
      throw e
    }
  }
  const unavailable = () => new ViewsClientError('unavailable', NO_ROWS)

  const client: ViewsClient = {
    list: (kind) => wrap(async () => {
      const found = viewFiles(await files.list()).filter((f) => kind === undefined || f.kind === kind)
      return Promise.all(found.map(async (f): Promise<DocumentSummary> => {
        const read = await files.read(f.path)
        const title = read ? validateText(read.content, { kind: f.kind, file: f.path }).document?.title : undefined
        return { name: f.name, kind: f.kind, title: typeof title === 'string' && title !== '' ? title : f.name }
      }))
    }),
    open: (kind, name) => wrap(async (): Promise<OpenedDocument> => {
      const file = await find(kind, name)
      const read = file ? await files.read(file.path) : null
      if (!file || !read) throw new ViewsClientError('not_found', `There is no ${kind} '${name}' in this workspace (${pathOf(kind, name)}).`)
      const checked = validateText(read.content, { kind, file: file.path })
      return { name, kind, text: read.content, version: read.hash, document: checked.document, diagnostics: checked.diagnostics }
    }),
    resolve: () => Promise.reject(unavailable()),
    validate: async (text, o) => validateText(text, o),
    save: (kind, name, text, expectedVersion) => wrap(async (): Promise<SaveResult> => {
      const existing = await find(kind, name)
      const conflict = async (path: string, why: string) => {
        const stored = (await files.read(path))?.hash
        return new ViewsClientError('version_conflict', `${why} The stored version is ${stored ?? 'unknown'}${expectedVersion ? `; yours was opened at ${expectedVersion}` : ''}.`)
      }
      if (existing) {
        if (expectedVersion === undefined) throw await conflict(existing.path, `${existing.path} exists already.`)
        try { return { name, version: (await files.write(existing.path, text, expectedVersion)).hash } } catch (e) {
          if (e instanceof FileRefusal && e.code === 'DDB-471') throw await conflict(existing.path, `${existing.path} was changed elsewhere.`)
          throw e
        }
      }
      if (expectedVersion !== undefined) throw new ViewsClientError('not_found', `${pathOf(kind, name)} is gone: it was deleted or merged away since you opened it.`)
      try { return { name, version: (await files.create(pathOf(kind, name), text)).hash } } catch (e) {
        if (e instanceof FileRefusal && e.code === 'DDB-471') throw await conflict(pathOf(kind, name), `${pathOf(kind, name)} exists already.`)
        throw e
      }
    }),
    delete: (kind, name) => wrap(async () => {
      const file = await find(kind, name)
      if (!file) throw new ViewsClientError('not_found', `There is no ${kind} '${name}' in this workspace.`)
      await files.remove(file.path, file.hash)
    }),
    preview: (request) => (options.rows ? options.rows.preview(request) : Promise.reject(unavailable())),
  }
  if (options.rows?.previewDocument) client.previewDocument = (request) => options.rows!.previewDocument!(request)
  if (options.catalog) {
    client.catalog = async (model: string): Promise<Catalog> => {
      const body = await options.catalog!(model)
      const found = catalogFromModel((body as { catalog?: unknown } | null)?.catalog ?? body)
      if (!found) throw new ViewsClientError('bad_response', `The model '${model}' has no measures or attributes the editor can list.`)
      return { ...found, model: found.model || model }
    }
  }
  return client
}
