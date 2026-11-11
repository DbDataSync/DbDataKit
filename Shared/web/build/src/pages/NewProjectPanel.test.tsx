import { isValidElement, type ReactElement, type ReactNode } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { ApiError } from '@dbdatakit/shell'
import { NewProjectPanel, NewProjectView, newProjectFailure, projectNameProblem, type NewProjectViewProps } from './NewProjectPanel'

const templates = [
  { name: 'retail', description: 'Northstar Outfitters, an outdoor retailer.', files: 100 },
  { name: 'starter', description: 'A small project.', files: 30 },
]

function props(over: Partial<NewProjectViewProps> = {}): NewProjectViewProps {
  return { templates, template: '', name: '', busy: false, failure: null, onTemplate: () => {}, onName: () => {}, onCreate: () => {}, onCancel: () => {}, ...over }
}

function find(node: ReactNode, testId: string): ReactElement<Record<string, unknown>> | undefined {
  if (Array.isArray(node)) { for (const child of node) { const found = find(child, testId); if (found) return found } return undefined }
  if (!isValidElement(node)) return undefined
  const element = node as ReactElement<Record<string, unknown>>
  if (element.props['data-testid'] === testId) return element
  if (typeof element.type === 'function') return find((element.type as (p: unknown) => ReactNode)(element.props), testId)
  return find(element.props.children as ReactNode, testId)
}

const panel = (catalog: unknown) => {
  const client = new QueryClient()
  client.setQueryData(['build', 'templates'], catalog)
  return renderToStaticMarkup(<QueryClientProvider client={client}><MemoryRouter><NewProjectPanel /></MemoryRouter></QueryClientProvider>)
}

describe('New project', () => {
  it('lists the server’s templates with their descriptions and file counts, none chosen', () => {
    const html = renderToStaticMarkup(NewProjectView(props()))
    expect(html).toContain('build-new-project-template-retail')
    expect(html).toContain('Northstar Outfitters')
    expect(html).toContain('(30 files)')
    expect(html).not.toContain('checked')
  })

  it('cannot create until a template is chosen and the name is a valid project name', () => {
    const disabled = (over: Partial<NewProjectViewProps>) => find(NewProjectView(props(over)), 'build-new-project-create')?.props.disabled
    expect(disabled({})).toBe(true)
    expect(disabled({ template: 'retail' })).toBe(true)
    expect(disabled({ name: 'shop' })).toBe(true)
    expect(disabled({ template: 'retail', name: 'Has Spaces' })).toBe(true)
    expect(disabled({ template: 'retail', name: 'shop', busy: true })).toBe(true)
    expect(disabled({ template: 'retail', name: 'shop' })).toBe(false)
  })

  it('says what a name may be, only when it is not', () => {
    expect(projectNameProblem('')).toBeNull()
    expect(projectNameProblem('retail-demo_2')).toBeNull()
    expect(projectNameProblem('Retail')).toContain('lower-case')
    expect(renderToStaticMarkup(NewProjectView(props({ name: 'No Good' })))).toContain('build-new-project-name-problem')
  })

  it('passes the choices up and makes the project on the button', () => {
    const calls: string[] = []
    const view = NewProjectView(props({ template: 'retail', name: 'shop', onTemplate: (t) => calls.push(`template ${t}`), onName: (n) => calls.push(`name ${n}`), onCreate: () => calls.push('create'), onCancel: () => calls.push('cancel') }))
    ;(find(view, 'build-new-project-create')!.props.onClick as () => void)()
    ;(find(view, 'build-new-project-cancel')!.props.onClick as () => void)()
    ;(find(view, 'build-new-project-name')!.props.onChange as (e: unknown) => void)({ target: { value: 'other' } })
    expect(calls).toEqual(['create', 'cancel', 'name other'])
  })

  it('shows a refusal as a plain sentence, with no trace', () => {
    expect(newProjectFailure(new ApiError(409, 'This server already hosts a project called \'shop\'.'))).toBe('This server already hosts a project called \'shop\'.')
    expect(newProjectFailure(new ApiError(403, 'Forbidden'))).toBe('Making a project needs the author role on this server.')
    expect(newProjectFailure(new TypeError('Failed to fetch'))).toBe('The server could not be reached, so no project was made.')
    expect(newProjectFailure(null)).toBeNull()
    expect(renderToStaticMarkup(NewProjectView(props({ failure: 'Nope.' })))).toContain('build-new-project-failed')
  })

  it('is offered to someone the server lets make a project, and to nobody else', () => {
    expect(panel({ templates, can_create: true })).toContain('build-new-project-open')
    expect(panel({ templates, can_create: false })).not.toContain('build-new-project')
    expect(panel({ templates, can_create: false })).toBe('')
  })
})
