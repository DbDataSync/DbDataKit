import { DashboardView, type DashboardViewProps } from '../dashboard/DashboardView'

/**
 * A report in its container (plan 32 DBR14): the document's `flow` layout, paged and scrolling, with page-break guides. It is `DashboardView` with the kind `report`: the layout is selected, the widgets are resolved as
 * the viewer, and a `flow` layout is drawn by `PagedFrame`; a report that also carries fit layouts for the screen (a canvas for a wall) shows them by their selectors, as a dashboard does.
 */
export function ReportView(props: DashboardViewProps) {
  return <DashboardView kind="report" {...props} />
}
