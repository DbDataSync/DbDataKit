export interface DocsModule { id: string; title: string; dir: string; order?: string; package?: string }
export interface GeneratedDocs { files: Map<string, string | Buffer>; problems: string[] }
export function readOrder(text: string): { slug: string; title: string }[]
export function generateModuleDocs(module: DocsModule): GeneratedDocs & { pages: { slug: string; title: string }[] }
export function discoverModules(webRoot: string): DocsModule[]
export function generateAllDocs(webRoot: string): GeneratedDocs
