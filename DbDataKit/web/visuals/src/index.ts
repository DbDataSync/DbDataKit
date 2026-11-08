// @dbdatakit/visuals: the shared visuals of the suite (plan 16 section 2a, plan 19 lane Y). Imports nothing from any product (a test checks).
export { validateRenderedView, validateVegaLiteSubset, validateViewDocument } from './validate'
export type { ViewIssue } from './validate'
export { isSafeTextLink } from './views/common'
export type { ViewsIssue } from './views/common'
export { validateWidgets } from './views/widgets'
export type { DrillTarget } from './views/widgets'
export { VIEWS_FORMAT, validateDashboard, validateReport, validateViewsDocument } from './views/document'
export type { ViewKind, ViewsOptions } from './views/document'
export { matches as layoutMatches, selectLayout, specificity as layoutSpecificity } from './views/selectLayout'
export type { LayoutChoice, LayoutContext, LayoutSelection, Medium, SelectableDocument, SelectableLayout } from './views/selectLayout'
export type {
  Box, ChartDoc, ControlDoc, ControlKind, ImageDoc, LayoutDoc, MatrixDoc, MetricDoc, ParameterDefinition, ParameterValue, ParameterValues, ReflowLayoutDoc, CanvasLayoutDoc, FlowLayoutDoc, TableColumnDoc, TableDoc, TextDoc,
  TileDoc, ViewsDoc, WidgetDoc, WidgetOptionsDoc, WidgetType,
} from './views/model'
export type {
  RenderedControl, RenderedImage, RenderedMetric, RenderedNumber, RenderedTable, RenderedText, RenderedWidget, RenderedWidgetError, RenderedWidgets,
} from './views/rendered'
export {
  FIXTURE_AS_OF, NO_RECORDED_IMAGE, NO_RECORDED_RESULT, ViewsClientError, fixtureClient, moduleClient, placedWidgets, textVersion, validateText,
} from './client'
export type {
  ClientDiagnostic, DocumentSummary, FixtureBundle, FixtureClientOptions, ModuleClientOptions, OpenedDocument, PreviewRequest, ResolveOptions, SaveResult, ValidationResult, ViewsClient, ViewsClientErrorCode,
} from './client'
export { RenderedViews } from './RenderedViews'
export type { RenderedViewsProps } from './RenderedViews'
export { MatrixGrid } from './MatrixGrid'
export { DataTable } from './DataTable'
export type { DataTableProps } from './DataTable'
export { VegaChart } from './VegaChart'
export { chartConfig, useTheme } from './theme'
export type { Theme, ThemeChoice } from './theme'
export type { DataValue, RenderedChart, RenderedError, RenderedMatrix, RenderedViewDocument } from './types'
export { LineageGraphView } from './lineage/LineageGraphView'
export type { LineageGraphViewProps } from './lineage/LineageGraphView'
export { projectLineage } from './lineage/project'
export { layoutLineage } from './lineage/layout'
export type { LineageGraphDoc, LineageNodeDoc, LineageEdgeDoc, LineageCard, LineageLink, LineageProjection, PlacedCard } from './lineage/types'
export { reach } from './lineage/reach'
export type { Reach } from './lineage/reach'
export type { CardState } from './lineage/LineageGraphView'
