# NAVIGATION-1 (work in progress)

## Built

- `Shared/web/shell/src/components/nav.ts`: groups (`NavGroup`), the admin filter and empty-group hiding, the lit entry (`pages` under an entry),
  `startsCollapsed`. `Shell.tsx` draws groups with labels, a collapse button (`nav-toggle`), the choice kept in `localStorage`
  (`dbdatakit.nav.collapsed`), collapsed under 1000 px. A flat `RailItem[]` is one untitled group (Gateway, Runner, Workspace unchanged).
- `ConsoleShell.tsx`: `consoleNav`, the README groups, one icon per entry, every `rail-*` id kept.
- Tabs: Views (`TabLinks`), Agents, Knowledge, Delivery, Backlog in the shell's `tabbar`; the Gateway's Server settings and a workspace's row as `subtabbar`.
- vitest `console/src/navigation.test.ts`; browser story `navigation.spec.ts`; `signInNewPerson` in `people.ts`.

## Defaults taken

- D1: the console's Servers group has only "All servers" until NAVIGATION-2; a viewer sees no Servers group at all.
- D2: under 1000 px the navigation always starts collapsed, even when the person last left it open; above, it follows their choice and is open when
  they never chose. Example: a person who opened it on a laptop opens a dashboard on an 800 px kiosk: the rail is icons; on the laptop again it is open.
- D3: Workspace tab row and the Gateway's Server settings Form/server.yaml toggle became `subtabbar` (a level down, inside the page), the console pages'
  tab rows became the shell's `tabbar` under the breadcrumb.
- D4: "Projects" waits for NAVIGATION-2 (it needs the paired Workspace).
