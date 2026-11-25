// @dbdatakit/workspace: the Workspace server's screens, mountable in a host (plan 22a, GP31).
export { WorkspaceModule } from './WorkspaceModule'
export { useSyncEngines, syncEnginesFrom, registryDescriptors, roleWords, SYNC_SOURCE, SYNC_TARGET } from './syncEngines'
export type { EngineRole, RoleStatus, SyncEngine, SyncEngineList, SyncRole } from './syncEngines'
export { registerDesigner, designerOf, designerPath, filesPath, buildsPath } from './designers'
export type { DesignerProps, DesignerRegistration } from './designers'
export { DraftProvider, useDraft, useDraftFile, useDraftOrNull, useDraftFiles, findingsKey } from './draft'
export type { Draft, DraftFile } from './draft'
export { workspaceNav } from './WorkspaceShell'
export { CATALOG_CACHE_FILE, CATALOG_CACHE_VERSION, readCatalogCache, readLine, schemasOf, tableLabel } from './catalogCache'
export type { CacheColumn, CacheConnection, CacheDatabase, CacheFinding, CacheReadAt, CacheTable, CatalogCache } from './catalogCache'
export { SYNC_TASK_CURRENT, connectionPath, connectionsOf, replicationPath, replicationsOf, syncNameProblem, withCurrentTaskVersion } from './syncSchemas'
export type { CheckDiagnostic, ProvisioningEndpoint, ProvisioningMapping, ProvisioningPlan, ProvisioningSide, ProvisioningStep, ToolAnswer } from './api'
export { writeColumns } from './mappingColumns'
export type { ColumnChoices } from './mappingColumns'
// the read of a connection's shape from a Runner channel, through the console, for every kind's Designer and the Files area's Catalog tab
export { CatalogRefresh, ChannelPicker, RefreshDifference } from './pages/CatalogRefresh'
export { catalogReadApi, differenceLine, isUnchanged, productionWarning, refreshDifference, refreshOutcome, withRefresh, workspaceServerOf } from './catalogRefresh'
export type { CatalogDifference, CatalogReadAnswer, CatalogReadRequest, ChannelChoice, ChannelList, ColumnChange, RefreshOutcome, RunnerChoice } from './catalogRefresh'
// the Designer's other shared reads, through the console: Set up for development, Test, Describe and the newest-check card
export { BindingTest, DevelopmentSetUp, NewestCheck, NewestChecks, QueryDescribe, RunAnswerStatus, useNewestRun, usePublishedChannels, useRunAnswer } from './pages/AuthoringReads'
export { findingsSummary, PREFLIGHT_STEP, PREFLIGHT_TASK, PreflightFindings, preflightPartsOf } from './pages/PreflightCard'
export type { PreflightFinding, PreflightPart, PreflightSeverity } from './pages/PreflightCard'
export type { AnswerAsk } from './pages/AuthoringReads'
export { useChannelChoice } from './pages/CatalogRefresh'
export { authoringReadApi, describedLine, newestPerChannel, PREVIEW_TASK, previewLine, previewValue, setUpChannels, setUpOffered, testLine, TEST_STEP_WORDS, trialLine, TRY_STRATEGY_TASK } from './authoringReads'
export type {
  DescribeAnswer, DescribedColumn, DescribeRequest, DraftTestAnswer, DraftTestRequest, MappingPreviewAnswer, NewestRun, PreviewValue, RunAnswer, StrategyCandidate, StrategyTrialAnswer, TestStep, TestStepName,
} from './authoringReads'
// a development login, sent to the console, which puts it on a Runner; the Workspace server never receives it
export { developmentLoginApi, developmentLoginLine, developmentLoginWarning, developmentLoginWarnings } from './developmentLogin'
export type { DevelopmentLoginAnswer, DevelopmentLoginRequest } from './developmentLogin'
// what a kind's Designer reads through the frame: the draft's check, where it stands against its newest deploy to a Runner, a replication's provisioning plan and its apply
export { commitsAfter, errorsUnder, provisioningPlanKey, useDraftCheck, useDraftPublication, useProvisioningApply, useProvisioningPlan } from './designerData'
export type { DraftPublication, ProvisioningApply } from './designerData'
export { RunResult as ProvisioningRunResult } from './pages/SyncProvisioningPanel'
export type { RunnerVersion } from './deploys'
