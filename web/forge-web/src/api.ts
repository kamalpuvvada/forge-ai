import type { EngineeringTask, EngineeringTaskSummary, ModelCall, ModelTelemetry, SystemCapabilities, VerificationCaseResult, VerificationFailureDetails } from './types'

interface ProblemDetails { title?: string; detail?: string; code?: string; errors?: Record<string, string[]> }

export class ForgeApiError extends Error {
  readonly code?: string
  constructor(message: string, code?: string) { super(message); this.code = code }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } })
  if (!response.ok) await throwResponseError(response)
  return response.json() as Promise<T>
}

const workflowStatuses = new Set([
  'Draft', 'Clarifying', 'RequirementSummaryReady', 'AwaitingRequirementApproval', 'ReadyForPlanning',
  'Planning', 'AwaitingPlanApproval', 'PlanApproved', 'Implementing', 'AwaitingImplementationReview',
  'ImplementationApproved', 'VerificationPlanning', 'AwaitingManualVerification', 'ManualVerificationFailed',
  'FailureAnalysisPending', 'FailureAnalysisRecoveryRequired', 'AwaitingFailureResolution', 'AwaitingCorrectionApproval', 'CorrectionApproved', 'ImplementingCorrection', 'CorrectionRecoveryRequired',
  'ReadyForDelivery', 'AwaitingDeliveryApproval', 'Delivering', 'PullRequestCreated', 'DeliveryRecoveryRequired',
  'Validating', 'Reviewing', 'Completed', 'Failed',
])
const verificationStatuses = new Set([
  'NotStarted', 'Active', 'FailedBeforeDispatch', 'RetryableProviderResponse', 'RejectedProviderOutput', 'AmbiguousAfterDispatch',
  'InterruptedBeforeDispatch', 'Completed',
])
const safeRetryStatuses = new Set(['FailedBeforeDispatch', 'RetryableProviderResponse', 'RejectedProviderOutput', 'InterruptedBeforeDispatch'])
export const invalidVerificationEligibilityMessage = 'Verification generation state could not be validated. Reload the task before taking action.'

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
const usageStates = new Set(['Complete', 'Partial', 'Unavailable'])
const responseStates = new Set(['Unknown', 'Queued', 'InProgress', 'Completed', 'Incomplete', 'Failed', 'Cancelled'])
const attemptStates = new Set(['Prepared', 'DispatchMayHaveStarted', 'ResponseReceived', 'Completed', 'FailedBeforeDispatch', 'RetryableProviderResponse', 'RejectedProviderOutput', 'AmbiguousAfterDispatch', 'InterruptedBeforeDispatch'])
const caseResults = new Set(['NotStarted', 'Passed', 'Failed', 'Blocked', 'NotApplicable'])
const manualAttemptStates = new Set(['InProgress', 'CompletedPassed', 'CompletedFailed'])
const testCategories = new Set(['Build', 'UnitTest', 'IntegrationTest', 'EndToEnd', 'LintOrStaticAnalysis', 'ManualBehavior', 'Regression', 'Security', 'DataOrMigration', 'Other'])
const dispatchStates = new Set(['DefinitelyNotDispatched', 'PossiblyDispatched', 'ResponseReceived'])
const modelStages = new Set(['Clarification', 'Planning', 'Implementation', 'VerificationPlanning', 'FailureAnalysis'])
const plannedFileActions = new Set(['Modify', 'Create', 'Delete', 'Inspect'])
const implementationOperationActions = new Set(['Create', 'Modify', 'Delete'])
const workspacePhases = new Set(['Reserved', 'Ready', 'RecoveryRequired', 'Completed', 'WorkspacePreparing',
  'WorkspacePrepared', 'MutationStarted', 'ApplyCompleted', 'ResultPersisted', 'Interrupted'])
const runtimeDispositions = new Set(['None', 'Active', 'SafeResume', 'RecoveryRequired', 'Interrupted',
  'TerminalIncompatible', 'Completed'])
const sha256Pattern = /^[0-9a-f]{64}$/

const isGuid = (value: unknown): value is string => typeof value === 'string' && guidPattern.test(value)
const isSafeString = (value: unknown, maximum = 10_000): value is string => typeof value === 'string' && value.length <= maximum
const isNullableString = (value: unknown, maximum = 10_000) => value === null || isSafeString(value, maximum)
const isTimestamp = (value: unknown) => isSafeString(value, 80) && Number.isFinite(Date.parse(value))
const isNullableTimestamp = (value: unknown) => value === null || isTimestamp(value)
const isSafeInteger = (value: unknown): value is number => Number.isSafeInteger(value) && (value as number) >= 0
const isNullableToken = (value: unknown) => value === null || isSafeInteger(value)
const isCost = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value) && value >= 0 && value <= 1_000_000_000
const isNullableCost = (value: unknown) => value === null || isCost(value)
const isStringArray = (value: unknown, maximumItems = 200, maximumLength = 10_000) =>
  Array.isArray(value) && value.length <= maximumItems && value.every(item => isSafeString(item, maximumLength))
const isNullableGuid = (value: unknown) => value === null || isGuid(value)
const isSha256 = (value: unknown) => typeof value === 'string' && sha256Pattern.test(value)
const isGitSha = (value: unknown) => typeof value === 'string' && /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/.test(value)
const isNullableSha256 = (value: unknown) => value === null || isSha256(value)
const isBoolean = (value: unknown) => typeof value === 'boolean'
const isNumberInRange = (value: unknown, minimum: number, maximum: number) =>
  typeof value === 'number' && Number.isFinite(value) && value >= minimum && value <= maximum
const isSequential = (values: number[]) => values.every((value, index) => value === index + 1)
const areUnique = (values: string[]) => new Set(values).size === values.length

function decodeClarificationAnswer(value: unknown) {
  return isRecord(value) && isSafeString(value.question, 10_000) && isSafeString(value.answer, 20_000) &&
    isTimestamp(value.answeredAt)
}

function decodeRequirementRevision(value: unknown) {
  return isRecord(value) && isSafeString(value.correction, 10_000) &&
    isSafeString(value.previousSummary, 20_000) && isTimestamp(value.submittedAt)
}

function decodePlanRevision(value: unknown) {
  return isRecord(value) && isSafeString(value.correction, 10_000) && isTimestamp(value.submittedAt) &&
    isSafeString(value.previousPlanTitle, 1_000) && isSafeString(value.previousRepositoryFingerprint, 256) &&
    isStringArray(value.previousAffectedPaths, 20, 1_000)
}

function decodeRepositoryFile(value: unknown) {
  return isRecord(value) && isSafeString(value.relativePath, 1_000) && isSafeString(value.extension, 40) &&
    isSafeInteger(value.sizeBytes) && isSafeInteger(value.lineCount) && isSafeString(value.probableRole, 200) &&
    isBoolean(value.isTest) && isNullableString(value.association, 1_000) &&
    isStringArray(value.declaredSymbols, 500, 500)
}

function decodeRepositorySnapshot(value: unknown) {
  return value === null || isRecord(value) && isBoolean(value.isGitRepository) &&
    isNullableString(value.branch, 500) && isNullableString(value.shortHeadSha, 128) &&
    isNullableString(value.fullHeadSha, 128) && isSafeString(value.workingTreeStatus, 100) &&
    isSafeInteger(value.totalDiscoveredFiles) && isSafeInteger(value.eligibleTextFileCount) &&
    isSafeInteger(value.excludedFileCount) && value.eligibleTextFileCount <= value.totalDiscoveredFiles &&
    isStringArray(value.detectedLanguages, 100, 100) && isStringArray(value.detectedExtensions, 500, 40) &&
    isStringArray(value.projectFiles, 500, 1_000) && isStringArray(value.testLocations, 500, 1_000) &&
    isStringArray(value.warnings, 100, 2_000) && isTimestamp(value.analyzedAt) &&
    isSafeString(value.fingerprint, 256) && Array.isArray(value.files) && value.files.length <= 10_000 &&
    value.files.every(decodeRepositoryFile)
}

function decodeEvidenceItem(value: unknown) {
  return isRecord(value) && isSafeString(value.id, 80) && isSafeString(value.relativePath, 1_000) &&
    isSafeInteger(value.startLine) && value.startLine > 0 && isSafeInteger(value.endLine) &&
    value.endLine >= value.startLine && isSafeString(value.excerpt, 100_000) &&
    isSafeString(value.reasonSelected, 2_000) && isSafeInteger(value.score) &&
    isSafeString(value.contentHash, 128)
}

function decodeImplementationPlan(value: unknown) {
  if (value === null) return true
  if (!isRecord(value) || !isSafeString(value.title, 1_000) || !isSafeString(value.objective, 10_000) ||
      !isSafeString(value.repositoryUnderstanding, 20_000) || !Array.isArray(value.affectedFiles) ||
      value.affectedFiles.length > 20 || !Array.isArray(value.orderedSteps) || value.orderedSteps.length > 20 ||
      !isStringArray(value.proposedValidationCommands, 20, 2_000) || !isStringArray(value.risks, 20, 2_000) ||
      !isStringArray(value.assumptions, 20, 2_000) || !isStringArray(value.unresolvedQuestions, 20, 2_000) ||
      !Array.isArray(value.requirementCoverage) || value.requirementCoverage.length > 100 ||
      !isSafeString(value.summary, 20_000) || !['DeterministicFake', 'OpenAI'].includes(value.source as string) ||
      !isNullableString(value.planningModel, 200) || !isBoolean(value.isDeterministicFake) ||
      value.isDeterministicFake !== (value.source === 'DeterministicFake') || !isTimestamp(value.createdAt) ||
      !isSafeString(value.repositoryFingerprint, 256)) return false
  const filesValid = value.affectedFiles.every(file => isRecord(file) && isSafeString(file.path, 1_000) &&
    plannedFileActions.has(file.action as string) && isSafeString(file.purpose, 10_000) &&
    isStringArray(file.evidenceIds, 100, 80) && isNumberInRange(file.confidence, 0, 1))
  const stepsValid = value.orderedSteps.every(step => isRecord(step) && isSafeInteger(step.order) &&
    step.order > 0 && isSafeString(step.description, 10_000) && isStringArray(step.affectedPaths, 100, 1_000) &&
    isStringArray(step.evidenceIds, 100, 80) && isSafeString(step.expectedResult, 10_000))
  const coverageValid = value.requirementCoverage.every(item => isRecord(item) &&
    isSafeString(item.requirement, 10_000) && isStringArray(item.affectedPaths, 100, 1_000) &&
    Array.isArray(item.stepOrders) && item.stepOrders.length <= 100 && item.stepOrders.every(order =>
      isSafeInteger(order) && order > 0))
  return filesValid && stepsValid && coverageValid &&
    isSequential(value.orderedSteps.map(step => (step as Record<string, unknown>).order as number))
}

function decodeImplementationWorkspace(value: unknown) {
  return value === null || isRecord(value) && isSafeString(value.branch, 500) &&
    isSafeString(value.baseCommitSha, 128) && workspacePhases.has(value.phase as string) &&
    isTimestamp(value.createdAt) && isTimestamp(value.updatedAt) &&
    Date.parse(value.updatedAt as string) >= Date.parse(value.createdAt as string) && isBoolean(value.isAvailable)
}

function decodeChangedFile(value: unknown) {
  return isRecord(value) && isSafeString(value.path, 1_000) &&
    implementationOperationActions.has(value.action as string) && isNullableSha256(value.originalContentSha256) &&
    isNullableSha256(value.newContentSha256) && isSafeInteger(value.originalBytes) && isSafeInteger(value.newBytes) &&
    isSafeInteger(value.originalLines) && isSafeInteger(value.newLines) && isSafeInteger(value.additions) &&
    isSafeInteger(value.deletions) && isSafeString(value.diffPreview, 2_000_000) &&
    isSafeInteger(value.fullDiffCharacters) && isSafeInteger(value.displayedDiffCharacters) &&
    value.displayedDiffCharacters <= value.fullDiffCharacters && isBoolean(value.diffTruncated) &&
    value.diffTruncated === (value.displayedDiffCharacters < value.fullDiffCharacters) &&
    isSafeInteger(value.fullDiffUtf8Bytes) && isSafeInteger(value.displayedDiffUtf8Bytes) &&
    value.displayedDiffUtf8Bytes <= value.fullDiffUtf8Bytes
}

function decodeImplementationResult(value: unknown) {
  return value === null || isRecord(value) && ['DeterministicFake', 'OpenAI'].includes(value.source as string) &&
    isNullableString(value.model, 200) && isSafeString(value.baseCommitSha, 128) && isSafeString(value.branch, 500) &&
    isSafeString(value.summary, 20_000) && isStringArray(value.warnings, 100, 2_000) &&
    Array.isArray(value.changedFiles) && value.changedFiles.length <= 100 && value.changedFiles.every(decodeChangedFile) &&
    isSafeInteger(value.fullDiffCharacters) && isSafeInteger(value.displayedDiffCharacters) &&
    value.displayedDiffCharacters <= value.fullDiffCharacters && isBoolean(value.diffTruncated) &&
    value.diffTruncated === (value.displayedDiffCharacters < value.fullDiffCharacters) &&
    isTimestamp(value.completedAt) && isBoolean(value.isDeterministicFake) &&
    value.isDeterministicFake === (value.source === 'DeterministicFake') &&
    isSafeInteger(value.fullDiffUtf8Bytes) && isSafeInteger(value.displayedDiffUtf8Bytes) &&
    value.displayedDiffUtf8Bytes <= value.fullDiffUtf8Bytes && isBoolean(value.activeCheckoutVerified)
}

function decodeImplementationFailure(value: unknown) {
  return value === null || isRecord(value) && isSafeString(value.category, 200) &&
    isSafeString(value.message, 2_000) && isBoolean(value.recoveryRequired) && isTimestamp(value.occurredAt) &&
    isBoolean(value.safeToResume) && isBoolean(value.activeCheckoutVerified)
}

function decodeImplementationRuntime(value: unknown) {
  return value === null || isRecord(value) && isBoolean(value.workspaceAvailable) &&
    isBoolean(value.activeCheckoutVerified) && runtimeDispositions.has(value.disposition as string) &&
    isNullableString(value.safeMessage, 2_000)
}

function decodeFailureDetails(value: unknown) {
  if (value === null) return true
  return isRecord(value) && isSafeString(value.title) && isSafeString(value.expectedResult) &&
    isSafeString(value.actualResult) && isStringArray(value.reproductionSteps) &&
    isStringArray(value.environmentNotes) && isNullableString(value.errorMessage) &&
    isStringArray(value.evidenceDescriptions) && ['Low', 'Medium', 'High', 'Critical'].includes(value.severity as string)
}

function decodeCaseResult(value: unknown) {
  return isRecord(value) && isGuid(value.resultRevisionId) && isSafeInteger(value.revisionNumber) &&
    isGuid(value.testCaseId) && caseResults.has(value.result as string) && isTimestamp(value.recordedAt) &&
    isNullableString(value.notes) && isNullableString(value.actualResult) &&
    isStringArray(value.evidenceDescriptions) && isNullableString(value.notApplicableReason) &&
    decodeFailureDetails(value.failureDetails) && isNullableGuid(value.supersedesResultRevisionId) &&
    isSafeString(value.trustLabel, 100)
}

function decodeTestStep(value: unknown) {
  return isRecord(value) && isSafeInteger(value.order) && value.order > 0 && isSafeString(value.instruction) &&
    isNullableString(value.approvedValidationCommandId, 100) && isSafeString(value.expectedObservation)
}

function decodeTestCase(value: unknown) {
  if (!isRecord(value) || !isGuid(value.testCaseId) || !isSafeInteger(value.order) || value.order <= 0 ||
    !isSafeString(value.title) || !isSafeString(value.objective) || !testCategories.has(value.category as string) ||
    typeof value.isRequired !== 'boolean' || !isStringArray(value.preconditions) || !isStringArray(value.testData) ||
    !Array.isArray(value.orderedSteps) || !value.orderedSteps.every(decodeTestStep) ||
    !isSafeString(value.expectedResult) || !isStringArray(value.negativeOrEdgeCases) ||
    !isStringArray(value.regressionScope) || !isStringArray(value.evidenceRequirements) ||
    !isStringArray(value.safetyNotes) || !(value.originTestCaseId === undefined || isNullableGuid(value.originTestCaseId)) ||
    !(value.regressionFailureReportIds === undefined || Array.isArray(value.regressionFailureReportIds) &&
      value.regressionFailureReportIds.every(isGuid) && areUnique(value.regressionFailureReportIds as string[]))) return false
  return isSequential(value.orderedSteps.map(step => (step as Record<string, unknown>).order as number))
}

function decodeVerificationPlan(value: unknown) {
  if (!isRecord(value) || !isGuid(value.planId) || !isSafeInteger(value.planNumber) || value.planNumber <= 0 ||
      !isGuid(value.implementationRevisionId) || !isSafeString(value.implementationResultFingerprint, 128) ||
      !isSafeString(value.approvedRequirementFingerprint, 128) || !isSafeString(value.approvedPlanFingerprint, 128) ||
      !isSafeString(value.generationContextFingerprint, 128) || !isSafeString(value.planFingerprint, 128) ||
      !isTimestamp(value.generatedAt) || !['DeterministicFake', 'OpenAI'].includes(value.source as string) ||
      !isNullableString(value.model, 200) || !isNullableString(value.reasoningEffort, 80) ||
      !isSafeString(value.summary) || !isSafeString(value.scope) || !isStringArray(value.preconditions) ||
      !Array.isArray(value.testCases) || !value.testCases.every(decodeTestCase) ||
      !isStringArray(value.risks) || !isStringArray(value.limitations) || !isStringArray(value.evidenceGuidance) ||
      !['Current', 'Superseded', 'Completed'].includes(value.status as string) ||
      !isSafeString(value.trustLabel, 100) || !isSafeString(value.executionLabel, 100) ||
      !(value.supersedesPlanId === undefined || isNullableGuid(value.supersedesPlanId)) ||
      !(value.regenerationReason === undefined || isNullableString(value.regenerationReason, 500))) return false
  const cases = value.testCases as Array<Record<string, unknown>>
  return areUnique(cases.map(testCase => testCase.testCaseId as string)) &&
    isSequential(cases.map(testCase => testCase.order as number))
}

function decodeProviderResponse(value: unknown) {
  if (!isRecord(value) || !isGuid(value.logicalCallId) || !isTimestamp(value.startedAt) ||
      !isTimestamp(value.receivedAt) || !responseStates.has(value.status as string) ||
      !usageStates.has(value.usageAvailability as string) || !isNullableToken(value.inputTokens) ||
      !isNullableToken(value.cachedInputTokens) || !isNullableToken(value.outputTokens) ||
      !isNullableToken(value.reasoningTokens) || !isSafeInteger(value.httpStatusCode) || value.httpStatusCode < 100 ||
      value.httpStatusCode > 599 || !isNullableString(value.providerResponseId, 200) ||
      !isNullableString(value.providerRequestId, 200) || !isNullableString(value.incompleteReason, 200) ||
      value.dispatchDisposition !== 'ResponseReceived') return false
  const available = value.usageAvailability !== 'Unavailable'
  return Date.parse(value.receivedAt as string) >= Date.parse(value.startedAt as string) &&
    validateUsage(value.usageAvailability as string, available, value.inputTokens, value.cachedInputTokens,
      value.outputTokens, value.reasoningTokens, null) &&
    (value.usageAvailable === undefined || typeof value.usageAvailable === 'boolean' && value.usageAvailable === available)
}

function decodeGenerationAttempt(value: unknown) {
  if (!isRecord(value) || !isGuid(value.commandId) || !isTimestamp(value.startedAt) ||
    !isTimestamp(value.leaseExpiresAt) || !isNullableTimestamp(value.completedAt) ||
    !attemptStates.has(value.status as string) || !Array.isArray(value.modelCallIds) || !value.modelCallIds.every(isGuid) ||
    !isNullableString(value.failureCategory, 200) || !isNullableString(value.failureMessage) ||
    !isNullableGuid(value.resultPlanId) || !isNullableGuid(value.lastLogicalCallId) ||
    !isSafeInteger(value.logicalCallCount) || !isSafeInteger(value.physicalRequestCount) ||
    !isSafeInteger(value.possiblyDispatchedRequestCount) ||
    !Array.isArray(value.logicalCalls) || !value.logicalCalls.every(call => isRecord(call) &&
      isGuid(call.logicalCallId) && isTimestamp(call.startedAt)) ||
    !Array.isArray(value.providerResponses) || !value.providerResponses.every(decodeProviderResponse)) return false
  const logicalCalls = value.logicalCalls as Array<Record<string, unknown>>
  const responses = value.providerResponses as Array<Record<string, unknown>>
  const logicalIds = logicalCalls.map(call => call.logicalCallId as string)
  return Date.parse(value.leaseExpiresAt as string) > Date.parse(value.startedAt as string) &&
    (value.completedAt === null || Date.parse(value.completedAt as string) >= Date.parse(value.startedAt as string)) &&
    value.logicalCallCount === logicalCalls.length && areUnique(logicalIds) &&
    areUnique(value.modelCallIds as string[]) && value.modelCallIds.length <= value.logicalCallCount &&
    areUnique(responses.map(response => response.logicalCallId as string)) &&
    responses.every(response => logicalIds.includes(response.logicalCallId as string) &&
      Date.parse(response.startedAt as string) >= Date.parse(value.startedAt as string)) &&
    (value.lastLogicalCallId === null || logicalIds.includes(value.lastLogicalCallId as string)) &&
    value.physicalRequestCount + value.possiblyDispatchedRequestCount <= value.logicalCallCount
}

function decodeManualAttempt(value: unknown) {
  return isRecord(value) && isGuid(value.attemptId) && isSafeInteger(value.attemptNumber) &&
    isGuid(value.verificationPlanId) && isSafeString(value.verificationPlanFingerprint, 128) &&
    isGuid(value.implementationRevisionId) && isSafeString(value.implementationResultFingerprint, 128) &&
    isTimestamp(value.startedAt) && isNullableTimestamp(value.completedAt) &&
    manualAttemptStates.has(value.status as string) && Array.isArray(value.resultRevisions) &&
    value.resultRevisions.every(decodeCaseResult) && Array.isArray(value.currentCaseResults) &&
    value.currentCaseResults.every(decodeCaseResult) &&
    (value.completionConfirmation === null || typeof value.completionConfirmation === 'boolean') &&
    isNullableString(value.summary) && isNullableString(value.attemptFingerprint, 128) &&
    isNullableTimestamp(value.passedAt) && isNullableTimestamp(value.failedAt) && isSafeString(value.trustLabel, 100) &&
    Date.parse(value.startedAt as string) <= (value.completedAt === null ? Number.POSITIVE_INFINITY : Date.parse(value.completedAt as string))
}

function decodeImplementationRevision(value: unknown) {
  return isRecord(value) && isGuid(value.revisionId) && isSafeInteger(value.revisionNumber) && value.revisionNumber > 0 &&
    ['Initial', 'Correction'].includes(value.kind as string) &&
    (value.previousRevisionId === null || isGuid(value.previousRevisionId)) &&
    isSafeString(value.planFingerprint, 128) && isSafeString(value.baseCommitSha, 128) &&
    isTimestamp(value.generationStartedAt) && isNullableTimestamp(value.generationCompletedAt) &&
    ['Requested', 'Generating', 'Succeeded', 'Failed'].includes(value.generationState as string) &&
    ['NotReviewable', 'Current', 'Superseded', 'Approved', 'HistoricallyApproved'].includes(value.reviewState as string) &&
    isNullableString(value.failureCategory, 200) && isNullableString(value.failureMessage) &&
    isNullableString(value.resultFingerprint, 128) && isSafeInteger(value.changedFileCount) &&
    isNullableTimestamp(value.correctionSubmittedAt) && isNullableTimestamp(value.approvedAt) &&
    typeof value.isCurrent === 'boolean' && typeof value.isApproved === 'boolean' &&
    (value.correctionProposalId === undefined || isNullableGuid(value.correctionProposalId)) &&
    (value.correctionProposalFingerprint === undefined || isNullableSha256(value.correctionProposalFingerprint))
}

function decodeApprovedOperation(value: unknown) {
  return isRecord(value) && isSafeString(value.path, 300) && implementationOperationActions.has(value.action as string)
}

function decodeFailureAnalysis(value: unknown) {
  return isRecord(value) && isGuid(value.analysisId) && isSafeInteger(value.analysisNumber) && value.analysisNumber > 0 &&
    isGuid(value.generationCommandId) && isSha256(value.contextFingerprint) && isGuid(value.failedAttemptId) &&
    isSha256(value.failedAttemptFingerprint) && isGuid(value.verificationPlanId) &&
    isSha256(value.verificationPlanFingerprint) && isGuid(value.implementationRevisionId) &&
    isSha256(value.implementationResultFingerprint) &&
    ['ImplementationDefect', 'ApprovedPlanDefect', 'ApprovedRequirementDefect', 'EnvironmentOrSetupIssue', 'InsufficientEvidence'].includes(value.classification as string) &&
    isNumberInRange(value.confidencePercent, 0, 100) && isSafeString(value.rootCauseSummary, 1200) &&
    isSafeString(value.rationale, 2000) && isStringArray(value.evidenceReferences, 24, 500) &&
    Array.isArray(value.affectedApprovedOperations) && value.affectedApprovedOperations.length <= 20 &&
    value.affectedApprovedOperations.every(decodeApprovedOperation) && isSafeString(value.correctionStrategy, 2000) &&
    isSafeString(value.expectedBehavior, 1200) && isSafeString(value.verificationImpact, 1200) &&
    isStringArray(value.risks, 8, 500) && ['DeterministicFake', 'OpenAI'].includes(value.source as string) &&
    isNullableString(value.model, 200) && isNullableString(value.reasoningEffort, 80) &&
    Array.isArray(value.modelCallIds) && value.modelCallIds.every(isGuid) && areUnique(value.modelCallIds as string[]) &&
    isSha256(value.analysisFingerprint) && value.status === 'Completed' && isTimestamp(value.createdAt) &&
    isSafeString(value.trustLabel, 100) && isSafeString(value.safeRoute, 1000)
}

function decodeCorrectionProposal(value: unknown) {
  return isRecord(value) && isGuid(value.proposalId) && isSafeInteger(value.proposalNumber) && value.proposalNumber > 0 &&
    isGuid(value.analysisId) && isSha256(value.analysisFingerprint) && isGuid(value.failedAttemptId) &&
    isSha256(value.failedAttemptFingerprint) && isGuid(value.previousApprovedRevisionId) &&
    isSha256(value.previousResultFingerprint) && isSha256(value.approvedRequirementFingerprint) &&
    isSha256(value.approvedPlanFingerprint) && isSafeString(value.originalBaseCommitSha, 128) &&
    Array.isArray(value.affectedApprovedOperations) &&
    value.affectedApprovedOperations.length > 0 && value.affectedApprovedOperations.length <= 20 &&
    value.affectedApprovedOperations.every(decodeApprovedOperation) && isSafeString(value.rootCauseSummary, 1200) &&
    isSafeString(value.correctionStrategy, 2000) && isSafeString(value.expectedBehavior, 1200) &&
    isSafeString(value.verificationImpact, 1200) && isStringArray(value.risks, 8, 500) &&
    isSha256(value.proposalFingerprint) && ['AwaitingApproval', 'Approved'].includes(value.status as string) &&
    isTimestamp(value.createdAt) && isNullableTimestamp(value.approvedAt)
}

function decodeCorrectionEligibility(value: unknown) {
  return isRecord(value) && ['canGenerateFailureAnalysis', 'canApproveCorrection', 'canGenerateCorrection',
    'canApproveCorrectedRevision', 'canGenerateReplacementVerificationPlan'].every(key => typeof value[key] === 'boolean')
}

function decodeFailureAnalysisAttempt(value: unknown) {
  const states = ['Prepared', 'DispatchMayHaveStarted', 'ResponseReceived', 'Completed',
    'FailedBeforeDispatch', 'RetryableProviderResponse', 'RejectedProviderOutput', 'AmbiguousAfterDispatch',
    'ExpiredBeforeDispatch', 'InterruptedAfterResponse']
  return isRecord(value) && isGuid(value.commandId) && isGuid(value.expectedFailedAttemptId) &&
    isSha256(value.expectedFailedAttemptFingerprint) && isTimestamp(value.startedAt) && isTimestamp(value.leaseExpiresAt) &&
    Date.parse(value.leaseExpiresAt as string) > Date.parse(value.startedAt as string) && isTimestamp(value.updatedAt) &&
    isNullableTimestamp(value.completedAt) &&
    Date.parse(value.updatedAt as string) >= Date.parse(value.startedAt as string) && states.includes(value.status as string) &&
    isSafeInteger(value.logicalCallCount) && isSafeInteger(value.physicalRequestCount) &&
    isSafeInteger(value.possiblyDispatchedRequestCount) && isSafeInteger(value.definitelyUndispatchedRequestCount) &&
    isSafeInteger(value.activeRequestCount) && value.physicalRequestCount + value.possiblyDispatchedRequestCount +
      value.definitelyUndispatchedRequestCount + value.activeRequestCount === value.logicalCallCount &&
    Array.isArray(value.modelCallIds) &&
    value.modelCallIds.every(isGuid) && areUnique(value.modelCallIds as string[]) &&
    value.physicalRequestCount === value.modelCallIds.length && value.possiblyDispatchedRequestCount <= value.logicalCallCount &&
    Array.isArray(value.providerResponses) && value.providerResponses.every(decodeProviderResponse) &&
    isNullableGuid(value.resultAnalysisId) &&
    isNullableString(value.failureCategory, 200) && isNullableString(value.failureMessage, 1000) &&
    typeof value.retryEligible === 'boolean' && typeof value.recoveryRequired === 'boolean' &&
    value.retryEligible === ['FailedBeforeDispatch', 'RetryableProviderResponse', 'RejectedProviderOutput', 'ExpiredBeforeDispatch'].includes(value.status as string) &&
    value.recoveryRequired === ['AmbiguousAfterDispatch', 'InterruptedAfterResponse'].includes(value.status as string) &&
    (['Prepared', 'DispatchMayHaveStarted', 'ResponseReceived'].includes(value.status as string)
      ? value.completedAt === null : value.completedAt !== null)
}

function decodeCorrectionGenerationAttempt(value: unknown) {
  const states = ['Prepared', 'DispatchMayHaveStarted', 'ResponseReceived', 'OutputAccepted', 'CheckoutVerified',
    'RevisionReserved', 'WorkspacePreparing', 'WorkspacePrepared', 'MutationStarted', 'ApplyCompleted',
    'ResultPersisted', 'FailedBeforeDispatch', 'FailedBeforeMutation', 'AmbiguousAfterDispatch', 'InterruptedAfterResponse', 'RecoveryRequired', 'Completed']
  return isRecord(value) && isGuid(value.attemptId) && isGuid(value.commandId) && isGuid(value.proposalId) &&
    isSha256(value.proposalFingerprint) && isGuid(value.previousRevisionId) && isSha256(value.previousResultFingerprint) &&
    isTimestamp(value.startedAt) &&
    isTimestamp(value.leaseExpiresAt) && Date.parse(value.leaseExpiresAt as string) > Date.parse(value.startedAt as string) &&
    isNullableTimestamp(value.completedAt) &&
    isTimestamp(value.updatedAt) && Date.parse(value.updatedAt as string) >= Date.parse(value.startedAt as string) &&
    states.includes(value.status as string) && isSafeInteger(value.logicalCallCount) &&
    isSafeInteger(value.physicalRequestCount) && isSafeInteger(value.possiblyDispatchedRequestCount) &&
    isSafeInteger(value.definitelyUndispatchedRequestCount) && isSafeInteger(value.activeRequestCount) &&
    value.physicalRequestCount + value.possiblyDispatchedRequestCount + value.definitelyUndispatchedRequestCount +
      value.activeRequestCount === value.logicalCallCount &&
    Array.isArray(value.modelCallIds) && value.modelCallIds.every(isGuid) && areUnique(value.modelCallIds as string[]) &&
    value.physicalRequestCount === value.modelCallIds.length && value.possiblyDispatchedRequestCount <= value.logicalCallCount &&
    Array.isArray(value.providerResponses) && value.providerResponses.every(decodeProviderResponse) &&
    (value.acceptedOutputFingerprint === null || isSha256(value.acceptedOutputFingerprint)) &&
    isNullableGuid(value.revisionId) && isNullableString(value.failureCategory, 200) &&
    isNullableString(value.failureMessage, 1000) && typeof value.retryEligible === 'boolean' &&
    typeof value.recoveryRequired === 'boolean' &&
    value.retryEligible === ['FailedBeforeDispatch', 'FailedBeforeMutation'].includes(value.status as string) &&
    value.recoveryRequired === ['AmbiguousAfterDispatch', 'InterruptedAfterResponse', 'RecoveryRequired'].includes(value.status as string) &&
    (['FailedBeforeDispatch', 'FailedBeforeMutation', 'AmbiguousAfterDispatch', 'InterruptedAfterResponse', 'RecoveryRequired', 'Completed'].includes(value.status as string)
      ? value.completedAt !== null : value.completedAt === null)
}

function decodeCorrectionApprovalAudit(value: unknown) {
  return isRecord(value) && isGuid(value.commandId) && isGuid(value.proposalId) &&
    isSha256(value.proposalFingerprint) && isSafeInteger(value.expectedRowVersion) &&
    isSafeInteger(value.completedRowVersion) && value.completedRowVersion === value.expectedRowVersion + 1 &&
    isTimestamp(value.createdAt) && isTimestamp(value.completedAt) &&
    Date.parse(value.completedAt as string) >= Date.parse(value.createdAt as string) && value.result === 'Approved'
}

function decodeDeliveryProposal(value: unknown) {
  return isRecord(value) && isGuid(value.deliveryProposalId) && value.proposalNumber === 1 &&
    isGuid(value.currentApprovedRevisionId) && isSha256(value.currentImplementationResultFingerprint) &&
    isGuid(value.currentVerificationPlanId) && isSha256(value.currentVerificationPlanFingerprint) &&
    isGuid(value.passedManualAttemptId) && isSha256(value.passedManualAttemptFingerprint) &&
    isGitSha(value.baseCommitSha) && value.remoteName === 'origin' && isSafeString(value.gitHubRepositoryOwner, 100) &&
    isSafeString(value.gitHubRepositoryName, 100) && value.targetBaseBranch === 'main' &&
    isGitSha(value.targetBaseCommitShaAtPreparation) && isSafeString(value.deliveryBranch, 160) &&
    /^forge-delivery-[a-z0-9-]+$/.test(value.deliveryBranch as string) &&
    isSafeString(value.commitMessage, 160) && !(value.commitMessage as string).includes('\n') &&
    isSafeString(value.pullRequestTitle, 120) && !(value.pullRequestTitle as string).includes('\n') &&
    isSafeString(value.pullRequestBody, 8192) && new TextEncoder().encode(value.pullRequestBody as string).length <= 8192 &&
    Array.isArray(value.changedPaths) && value.changedPaths.length > 0 && value.changedPaths.length <= 24 &&
    value.changedPaths.every(path => isSafeString(path, 300) && !/^(?:[a-z]:|[/\\]|.*(?:^|[/\\])\.\.(?:[/\\]|$))/i.test(path as string)) &&
    areUnique(value.changedPaths as string[]) && isSha256(value.proposalFingerprint) && isTimestamp(value.createdAt) &&
    ['Prepared', 'Approved', 'Delivered'].includes(value.status as string) && isNullableTimestamp(value.approvedAt) &&
    (value.status === 'Prepared' ? value.approvedAt === null : value.approvedAt !== null)
}

function decodeDeliveryAttempt(value: unknown) {
  const phases = ['Prepared', 'WorktreeVerified', 'StagingStarted', 'CommitCreated', 'PushStarted', 'BranchPushed',
    'PullRequestCreationStarted', 'PullRequestCreated', 'FailedBeforeMutation', 'RecoveryRequired']
  return isRecord(value) && isGuid(value.attemptId) && isSafeInteger(value.attemptNumber) &&
    value.attemptNumber >= 1 && value.attemptNumber <= 3 && isGuid(value.commandId) && isGuid(value.deliveryProposalId) &&
    isSha256(value.deliveryProposalFingerprint) && isTimestamp(value.startedAt) && isTimestamp(value.updatedAt) &&
    isNullableTimestamp(value.completedAt) && isTimestamp(value.leaseExpiresAt) &&
    Date.parse(value.updatedAt as string) >= Date.parse(value.startedAt as string) &&
    Date.parse(value.leaseExpiresAt as string) > Date.parse(value.startedAt as string) &&
    phases.includes(value.phase as string) && (value.commitSha === null || isGitSha(value.commitSha)) &&
    (value.remoteBranchSha === null || isGitSha(value.remoteBranchSha)) &&
    (value.pullRequestNumber === null || isSafeInteger(value.pullRequestNumber) && value.pullRequestNumber > 0) &&
    (value.pullRequestUrl === null || isSafeString(value.pullRequestUrl, 500) && /^https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/pull\/[1-9][0-9]*$/.test(value.pullRequestUrl as string)) &&
    isNullableString(value.safeFailureCategory, 100) && isNullableString(value.safeFailureMessage, 500) &&
    typeof value.recoveryRequired === 'boolean' && typeof value.activeCheckoutVerifiedBefore === 'boolean' &&
    typeof value.activeCheckoutVerifiedAfter === 'boolean' && typeof value.legacyCanonicalizationUsed === 'boolean' &&
    (['PullRequestCreated', 'FailedBeforeMutation', 'RecoveryRequired'].includes(value.phase as string)
      ? value.completedAt !== null : value.completedAt === null) &&
    (value.phase === 'RecoveryRequired') === value.recoveryRequired && validateDeliveryAttemptPhase(value)
}

function validateDeliveryAttemptPhase(value: Record<string, unknown>) {
  const noPullRequest = value.pullRequestNumber === null && value.pullRequestUrl === null
  const noFailure = value.safeFailureCategory === null && value.safeFailureMessage === null && value.recoveryRequired === false
  if (['Prepared', 'WorktreeVerified', 'StagingStarted'].includes(value.phase as string))
    return value.commitSha === null && value.remoteBranchSha === null && noPullRequest && noFailure
  if (['CommitCreated', 'PushStarted'].includes(value.phase as string))
    return value.commitSha !== null && value.remoteBranchSha === null && noPullRequest && noFailure
  if (['BranchPushed', 'PullRequestCreationStarted'].includes(value.phase as string))
    return value.commitSha !== null && value.remoteBranchSha === value.commitSha && noPullRequest && noFailure
  if (value.phase === 'PullRequestCreated')
    return value.commitSha !== null && value.remoteBranchSha === value.commitSha && value.pullRequestNumber !== null &&
      value.pullRequestUrl !== null && noFailure && value.activeCheckoutVerifiedAfter === true
  if (value.phase === 'FailedBeforeMutation')
    return value.commitSha === null && value.remoteBranchSha === null && noPullRequest &&
      value.safeFailureCategory !== null && value.safeFailureMessage !== null
  return value.phase === 'RecoveryRequired' && value.safeFailureCategory !== null && value.safeFailureMessage !== null
}

function decodeDeliveryEligibility(value: unknown) {
  return isRecord(value) && ['canPrepareDelivery', 'canApproveDelivery', 'canExecuteDelivery',
    'canReconcileDelivery', 'deliveryRecoveryRequired', 'pullRequestCreated'].every(key => typeof value[key] === 'boolean')
}

function validateDeliveryRelationships(task: Record<string, unknown>) {
  const proposals = (task.deliveryProposals ?? []) as Array<Record<string, unknown>>
  const attempts = (task.deliveryAttempts ?? []) as Array<Record<string, unknown>>
  const eligibility = (task.deliveryEligibility ?? {
    canPrepareDelivery: false, canApproveDelivery: false, canExecuteDelivery: false,
    canReconcileDelivery: false, deliveryRecoveryRequired: false, pullRequestCreated: false,
  }) as Record<string, unknown>
  const deliveryStatus = ['AwaitingDeliveryApproval', 'Delivering', 'PullRequestCreated', 'DeliveryRecoveryRequired']
    .includes(task.status as string)
  if (!deliveryStatus && proposals.length === 0 && attempts.length === 0)
    return Object.values(eligibility).every(value => value === false) ||
      task.status === 'ReadyForDelivery' && eligibility.canPrepareDelivery === true
  if (proposals.length !== 1 || task.currentDeliveryProposalId !== proposals[0].deliveryProposalId) return false
  const proposal = proposals[0]
  const revision = (task.implementationRevisions as Array<Record<string, unknown>>)
    .find(item => item.revisionId === proposal.currentApprovedRevisionId)
  const plan = (task.verificationPlans as Array<Record<string, unknown>>)
    .find(item => item.planId === proposal.currentVerificationPlanId)
  const manual = (task.manualVerificationAttempts as Array<Record<string, unknown>>)
    .find(item => item.attemptId === proposal.passedManualAttemptId)
  if (!revision || revision.resultFingerprint !== proposal.currentImplementationResultFingerprint ||
      !plan || plan.planFingerprint !== proposal.currentVerificationPlanFingerprint ||
      !manual || manual.attemptFingerprint !== proposal.passedManualAttemptFingerprint ||
      manual.status !== 'CompletedPassed' || manual.completionConfirmation !== true) return false
  if (attempts.length > 3 || attempts.some(attempt => attempt.deliveryProposalId !== proposal.deliveryProposalId ||
      attempt.deliveryProposalFingerprint !== proposal.proposalFingerprint)) return false
  if (!attempts.every((attempt, index) => attempt.attemptNumber === index + 1) ||
      !areUnique(attempts.map(attempt => attempt.attemptId) as string[]) ||
      !areUnique(attempts.map(attempt => attempt.commandId) as string[])) return false
  const currentAttemptId = task.currentDeliveryAttemptId
  if (attempts.length === 0 ? currentAttemptId !== null : currentAttemptId !== attempts.at(-1)?.attemptId) return false
  const activePhases = ['Prepared', 'WorktreeVerified', 'StagingStarted', 'CommitCreated', 'PushStarted',
    'BranchPushed', 'PullRequestCreationStarted']
  const activeAttempts = attempts.filter(attempt => activePhases.includes(attempt.phase as string))
  if (activeAttempts.length > 1 || activeAttempts.some(attempt => attempt.attemptId !== currentAttemptId)) return false
  const uniqueNullable = (values: unknown[]) => {
    const present = values.filter((value): value is string | number => value !== null)
    return new Set(present).size === present.length
  }
  if (!uniqueNullable(attempts.map(attempt => attempt.commitSha)) ||
      !uniqueNullable(attempts.map(attempt => attempt.remoteBranchSha)) ||
      !uniqueNullable(attempts.map(attempt => attempt.pullRequestNumber)) ||
      !uniqueNullable(attempts.map(attempt => attempt.pullRequestUrl))) return false
  if (attempts.slice(0, -1).some(attempt => attempt.phase !== 'FailedBeforeMutation' || attempt.recoveryRequired === true)) return false
  const latestAttempt = attempts.at(-1)
  const expected = {
    canPrepareDelivery: false,
    canApproveDelivery: task.status === 'AwaitingDeliveryApproval' && proposal.status === 'Prepared',
    canExecuteDelivery: task.status === 'AwaitingDeliveryApproval' && proposal.status === 'Approved' &&
      (attempts.length === 0 || attempts.length < 3 && latestAttempt?.phase === 'FailedBeforeMutation'),
    canReconcileDelivery: task.status === 'DeliveryRecoveryRequired' && latestAttempt?.phase === 'RecoveryRequired',
    deliveryRecoveryRequired: task.status === 'DeliveryRecoveryRequired',
    pullRequestCreated: task.status === 'PullRequestCreated',
  }
  if (!Object.entries(expected).every(([key, value]) => eligibility[key] === value)) return false
  if (task.status === 'Delivering') return latestAttempt !== undefined && !['PullRequestCreated', 'FailedBeforeMutation', 'RecoveryRequired'].includes(latestAttempt.phase as string)
  if (task.status === 'DeliveryRecoveryRequired') return latestAttempt?.phase === 'RecoveryRequired'
  if (task.status === 'PullRequestCreated') return proposal.status === 'Delivered' && latestAttempt?.phase === 'PullRequestCreated' &&
    latestAttempt.commitSha === latestAttempt.remoteBranchSha && latestAttempt.pullRequestUrl !== null &&
    latestAttempt.activeCheckoutVerifiedAfter === true
  return task.status === 'AwaitingDeliveryApproval' && (attempts.length === 0 || latestAttempt?.phase === 'FailedBeforeMutation')
}

function validateUsage(state: string, available: boolean, input: unknown, cached: unknown, output: unknown,
  reasoning: unknown, estimatedCost: unknown) {
  if (![input, cached, output, reasoning].every(isNullableToken) || !isNullableCost(estimatedCost)) return false
  if (cached !== null && (input === null || (cached as number) > (input as number))) return false
  if (reasoning !== null && (output === null || (reasoning as number) > (output as number))) return false
  const present = [input, cached, output, reasoning].filter(value => value !== null).length
  if (state === 'Complete') return available && present === 4
  if (state === 'Partial') return available && present > 0 && present < 4
  return state === 'Unavailable' && !available && present === 0 && estimatedCost === null
}

function decodeModelCall(value: unknown): ModelCall | null {
  if (!isRecord(value) || !isGuid(value.id) || !modelStages.has(value.stage as string) ||
      !isSafeString(value.provider, 200) || !isSafeString(value.model, 200) ||
      !isSafeString(value.reasoningEffort, 80) || !isTimestamp(value.startedAt) ||
      !isTimestamp(value.completedAt) || typeof value.succeeded !== 'boolean' ||
      !isNullableToken(value.inputTokens) || !isNullableToken(value.cachedInputTokens) ||
      !isNullableToken(value.uncachedInputTokens) || !isNullableToken(value.outputTokens) ||
      !isNullableToken(value.reasoningTokens) || !isNullableCost(value.estimatedCostUsd) ||
      !isSafeString(value.pricingProvenance, 200) || typeof value.hasStoredPricingSnapshot !== 'boolean' ||
      !isNullableString(value.failureCategory, 200) || !isNullableString(value.providerResponseId, 200) ||
      !isNullableString(value.providerRequestId, 200) ||
      !(value.providerUsageAvailability === undefined || value.providerUsageAvailability === null ||
        usageStates.has(value.providerUsageAvailability as string)) ||
      typeof value.usageAvailable !== 'boolean' || typeof value.isPartialEstimate !== 'boolean' ||
      !(value.verificationDispatchDisposition === undefined || value.verificationDispatchDisposition === null ||
        dispatchStates.has(value.verificationDispatchDisposition as string)) ||
      !(value.providerHttpStatusCode === undefined || value.providerHttpStatusCode === null ||
        isSafeInteger(value.providerHttpStatusCode) && value.providerHttpStatusCode >= 100 && value.providerHttpStatusCode <= 599) ||
      !(value.providerUsageAvailable === undefined || value.providerUsageAvailable === null ||
        typeof value.providerUsageAvailable === 'boolean') ||
      !(value.storedPricingSnapshot === null || isRecord(value.storedPricingSnapshot) &&
        isCost(value.storedPricingSnapshot.inputPerMillionUsd) &&
        isCost(value.storedPricingSnapshot.cachedInputPerMillionUsd) &&
        isCost(value.storedPricingSnapshot.outputPerMillionUsd)) ||
      (value.hasStoredPricingSnapshot === true) !== (value.storedPricingSnapshot !== null)) return null
  if (Date.parse(value.completedAt as string) < Date.parse(value.startedAt as string)) return null
  if (value.stage === 'VerificationPlanning' || value.stage === 'FailureAnalysis') {
    if (!usageStates.has(value.providerUsageAvailability as string) || typeof value.providerUsageAvailable !== 'boolean' ||
        value.providerUsageAvailable !== value.usageAvailable ||
        !validateUsage(value.providerUsageAvailability as string, value.usageAvailable as boolean, value.inputTokens,
          value.cachedInputTokens, value.outputTokens, value.reasoningTokens, value.estimatedCostUsd)) return null
  } else {
    if (value.providerUsageAvailability !== undefined && value.providerUsageAvailability !== null ||
        value.providerUsageAvailable !== undefined && value.providerUsageAvailable !== null &&
        (value.stage !== 'Implementation' || value.providerUsageAvailable !== value.usageAvailable ||
          value.verificationDispatchDisposition !== 'ResponseReceived' || value.providerHttpStatusCode === null ||
          value.providerHttpStatusCode === undefined)) return null
    if (value.usageAvailable) {
      if (value.inputTokens === null || value.outputTokens === null ||
          value.cachedInputTokens !== null && value.cachedInputTokens > value.inputTokens ||
          value.reasoningTokens !== null && value.reasoningTokens > value.outputTokens) return null
    } else if ([value.inputTokens, value.cachedInputTokens, value.uncachedInputTokens, value.outputTokens,
      value.reasoningTokens, value.estimatedCostUsd].some(item => item !== null)) return null
  }
  if (value.inputTokens !== null && value.cachedInputTokens !== null &&
      value.uncachedInputTokens !== value.inputTokens - value.cachedInputTokens ||
      value.inputTokens !== null && value.cachedInputTokens === null && value.uncachedInputTokens !== value.inputTokens ||
      value.inputTokens === null && value.uncachedInputTokens !== null) return null
  if (value.isPartialEstimate !== (value.estimatedCostUsd !== null && value.cachedInputTokens === null)) return null
  return value as unknown as ModelCall
}

function decodeTelemetry(value: unknown): ModelTelemetry | null {
  if (!isRecord(value) || !isSafeInteger(value.totalCalls) || !Array.isArray(value.calls)) return null
  const calls = value.calls.map(decodeModelCall)
  if (calls.some(call => call === null) || !usageStates.has(value.usageAvailability as string) ||
      !isSafeInteger(value.usageUnavailableCallCount) || !isNullableToken(value.totalInputTokens) ||
      !isNullableToken(value.totalCachedInputTokens) || !isNullableToken(value.totalOutputTokens) ||
      !isNullableToken(value.totalReasoningTokens) || !isNullableCost(value.totalEstimatedCostUsd) ||
      !isSafeInteger(value.costUnavailableCallCount) || typeof value.isPartialEstimate !== 'boolean' ||
      value.totalCalls !== value.calls.length || !isSafeInteger(value.verificationLogicalAttemptCount) ||
      !isSafeInteger(value.verificationPhysicalRequestCount) ||
      !isSafeInteger(value.verificationPossiblyDispatchedRequestCount) ||
      !isSafeInteger(value.verificationDefinitelyUndispatchedAttemptCount) ||
      !isNullableCost(value.completeEstimatedSubtotalUsd) || !isNullableCost(value.partialEstimatedSubtotalUsd) ||
      !isNullableCost(value.availableEstimatedSubtotalUsd) || typeof value.hasPartialEstimates !== 'boolean' ||
      !isSafeInteger(value.possiblyDispatchedUnavailableEstimatedCostCallCount))
    return null
  const validCalls = calls as ModelCall[]
  const unavailableUsage = validCalls.filter(call => !call.usageAvailable).length
  const hasPartialUsage = validCalls.some(call => call.providerUsageAvailability === 'Partial')
  const expectedUsage = validCalls.length === 0 || unavailableUsage === 0 && !hasPartialUsage ? 'Complete' :
    unavailableUsage === validCalls.length ? 'Unavailable' : 'Partial'
  if (value.usageAvailability !== expectedUsage || value.usageUnavailableCallCount !== unavailableUsage) return null
  const total = (field: 'inputTokens' | 'cachedInputTokens' | 'outputTokens' | 'reasoningTokens') =>
    validCalls.reduce((sum, call) => sum + (call[field] ?? 0), 0)
  if (expectedUsage === 'Complete') {
    if (value.totalInputTokens !== total('inputTokens') || value.totalOutputTokens !== total('outputTokens') ||
        value.totalCachedInputTokens !== (validCalls.every(call => call.cachedInputTokens !== null) ? total('cachedInputTokens') : null) ||
        value.totalReasoningTokens !== (validCalls.every(call => call.reasoningTokens !== null) ? total('reasoningTokens') : null)) return null
  } else if ([value.totalInputTokens, value.totalCachedInputTokens, value.totalOutputTokens,
      value.totalReasoningTokens].some(item => item !== null)) return null
  const costCalls = validCalls.filter(call => call.estimatedCostUsd !== null)
  const costUnavailable = validCalls.length - costCalls.length
  const completeCosts = costCalls.filter(call => !call.isPartialEstimate).map(call => call.estimatedCostUsd as number)
  const partialCosts = costCalls.filter(call => call.isPartialEstimate).map(call => call.estimatedCostUsd as number)
  const sum = (items: number[]) => items.reduce((total, item) => total + item, 0)
  const sameCost = (actual: unknown, expected: number | null) => actual === expected ||
    typeof actual === 'number' && expected !== null && Math.abs(actual - expected) <= 1e-9
  const expectedComplete = completeCosts.length > 0 ? sum(completeCosts) : null
  const expectedPartial = partialCosts.length > 0 ? sum(partialCosts) : null
  const expectedAvailable = costCalls.length > 0 ? sum(costCalls.map(call => call.estimatedCostUsd as number)) :
    validCalls.length === 0 ? 0 : null
  const hasPartialEstimates = partialCosts.length > 0
  const expectedTotalCost = costUnavailable === 0 && !hasPartialEstimates ? expectedAvailable : null
  if (value.costUnavailableCallCount !== costUnavailable || value.hasPartialEstimates !== hasPartialEstimates ||
      !sameCost(value.completeEstimatedSubtotalUsd, expectedComplete) ||
      !sameCost(value.partialEstimatedSubtotalUsd, expectedPartial) ||
      !sameCost(value.availableEstimatedSubtotalUsd, expectedAvailable) ||
      !sameCost(value.totalEstimatedCostUsd, expectedTotalCost) ||
      value.isPartialEstimate !== (costUnavailable > 0 || expectedUsage !== 'Complete')) return null
  return {
    ...(value as unknown as ModelTelemetry), calls: validCalls,
    completeEstimatedSubtotalUsd: isNullableCost(value.completeEstimatedSubtotalUsd) ? value.completeEstimatedSubtotalUsd as number | null : null,
    partialEstimatedSubtotalUsd: isNullableCost(value.partialEstimatedSubtotalUsd) ? value.partialEstimatedSubtotalUsd as number | null : null,
    availableEstimatedSubtotalUsd: isNullableCost(value.availableEstimatedSubtotalUsd) ? value.availableEstimatedSubtotalUsd as number | null : null,
  }
}


function normalizeVerificationEligibility(task: Record<string, unknown>) {
  const eligibility = task.verificationEligibility
  if (!isRecord(eligibility)) return null
  const canGenerate = eligibility.canGenerateVerificationPlan
  const initial = eligibility.isInitialVerificationPlanGeneration
  const canRetry = eligibility.canRetryVerificationPlanGeneration
  const status = eligibility.verificationGenerationStatus
  const message = eligibility.verificationGenerationStatusMessage
  if (typeof canGenerate !== 'boolean' || typeof initial !== 'boolean' || typeof canRetry !== 'boolean' ||
      typeof eligibility.canStartVerificationAttempt !== 'boolean' ||
      typeof eligibility.canRecordVerificationResult !== 'boolean' ||
      typeof eligibility.canCompleteVerificationPassed !== 'boolean' ||
      typeof eligibility.canCompleteVerificationFailed !== 'boolean' ||
      typeof eligibility.readyForDelivery !== 'boolean' ||
      !isNullableString(eligibility.ineligibilityReason, 1000) ||
      !(status === null || typeof status === 'string' && verificationStatuses.has(status)) ||
      !(message === null || typeof message === 'string' && message.length <= 1000)) return null
  if (!canGenerate && canRetry || initial && canRetry ||
      initial && status !== 'NotStarted' || canRetry && !safeRetryStatuses.has(status as string) ||
      canGenerate && status !== 'NotStarted' && !safeRetryStatuses.has(status as string) ||
      (status === 'AmbiguousAfterDispatch' || status === 'Completed' || status === 'Active') && (canGenerate || canRetry)) return null
  return eligibility
}

function canCompleteManualAttempt(
  plan: Record<string, unknown>, attempt: Record<string, unknown>, passed: boolean,
) {
  const currentResults = attempt.currentCaseResults as Array<Record<string, unknown>>
  if (!passed) return currentResults.some(result =>
    (result.result === 'Failed' || result.result === 'Blocked') && result.failureDetails !== null)
  if (currentResults.some(result => result.result === 'Failed' || result.result === 'Blocked')) return false
  if ((plan.testCases as Array<Record<string, unknown>>).some(testCase =>
    Array.isArray(testCase.regressionFailureReportIds) && testCase.regressionFailureReportIds.length > 0 &&
    currentResults.find(item => item.testCaseId === testCase.testCaseId)?.result !== 'Passed')) return false
  return (plan.testCases as Array<Record<string, unknown>>).filter(testCase => testCase.isRequired).every(testCase => {
    const result = currentResults.find(item => item.testCaseId === testCase.testCaseId)
    return !!result && (result.result === 'Passed' || result.result === 'NotApplicable') &&
      ((testCase.evidenceRequirements as unknown[]).length === 0 ||
        (result.evidenceDescriptions as unknown[]).length > 0)
  })
}

function validateCorrectionRelationships(task: Record<string, unknown>, telemetry: ModelTelemetry) {
  const analyses = (task.failureAnalyses ?? []) as Array<Record<string, unknown>>
  const proposals = (task.correctionProposals ?? []) as Array<Record<string, unknown>>
  const failureAttempts = (task.failureAnalysisGenerationAttempts ?? []) as Array<Record<string, unknown>>
  const correctionAttempts = (task.correctionGenerationAttempts ?? []) as Array<Record<string, unknown>>
  const approvals = (task.correctionApprovalAudit ?? []) as Array<Record<string, unknown>>
  const revisions = task.implementationRevisions as Array<Record<string, unknown>>
  const plans = task.verificationPlans as Array<Record<string, unknown>>
  const manualAttempts = task.manualVerificationAttempts as Array<Record<string, unknown>>
  if (!areUnique(analyses.map(item => item.analysisId as string)) ||
      !areUnique(failureAttempts.map(item => item.commandId as string)) ||
      !areUnique(correctionAttempts.map(item => item.commandId as string)) ||
      !areUnique(approvals.map(item => item.commandId as string))) return false
  const modelCalls = telemetry.calls
  for (const analysis of analyses) {
    const attempt = failureAttempts.find(item => item.commandId === analysis.generationCommandId)
    const failedAttempt = manualAttempts.find(item => item.attemptId === analysis.failedAttemptId)
    const failedPlan = plans.find(item => item.planId === analysis.verificationPlanId)
    const failedRevision = revisions.find(item => item.revisionId === analysis.implementationRevisionId)
    if (!attempt || attempt.status !== 'Completed' || attempt.resultAnalysisId !== analysis.analysisId ||
        !failedAttempt || failedAttempt.status !== 'CompletedFailed' ||
        failedAttempt.attemptFingerprint !== analysis.failedAttemptFingerprint ||
        failedAttempt.verificationPlanId !== analysis.verificationPlanId ||
        failedAttempt.verificationPlanFingerprint !== analysis.verificationPlanFingerprint ||
        failedAttempt.implementationRevisionId !== analysis.implementationRevisionId ||
        failedAttempt.implementationResultFingerprint !== analysis.implementationResultFingerprint ||
        !failedPlan || failedPlan.planFingerprint !== analysis.verificationPlanFingerprint ||
        failedPlan.implementationRevisionId !== analysis.implementationRevisionId ||
        failedPlan.implementationResultFingerprint !== analysis.implementationResultFingerprint ||
        !failedRevision || failedRevision.resultFingerprint !== analysis.implementationResultFingerprint ||
        (analysis.modelCallIds as string[]).some(id => !(attempt.modelCallIds as string[]).includes(id))) return false
    const calls = (analysis.modelCallIds as string[]).map(id => modelCalls.find(call => call.id === id))
    if (analysis.source === 'DeterministicFake') {
      if (analysis.model !== null || analysis.reasoningEffort !== null || calls.length !== 0) return false
    } else if (analysis.source !== 'OpenAI' || typeof analysis.model !== 'string' ||
        typeof analysis.reasoningEffort !== 'string' || calls.length === 0 || calls.some(call => !call ||
          call.stage !== 'FailureAnalysis' || call.provider !== 'OpenAI' || call.model !== analysis.model ||
          call.reasoningEffort.toLowerCase() !== (analysis.reasoningEffort as string).toLowerCase() ||
          call.verificationDispatchDisposition !== 'ResponseReceived')) return false
  }
  for (const proposal of proposals) {
    const analysis = analyses.find(item => item.analysisId === proposal.analysisId)
    if (!analysis || analysis.analysisFingerprint !== proposal.analysisFingerprint ||
        analysis.failedAttemptId !== proposal.failedAttemptId ||
        analysis.failedAttemptFingerprint !== proposal.failedAttemptFingerprint ||
        analysis.implementationRevisionId !== proposal.previousApprovedRevisionId ||
        analysis.implementationResultFingerprint !== proposal.previousResultFingerprint) return false
    const matches = approvals.filter(item => item.proposalId === proposal.proposalId)
    if (proposal.status === 'Approved' ? matches.length !== 1 || matches[0].proposalFingerprint !== proposal.proposalFingerprint :
        matches.length !== 0) return false
  }
  for (const attempt of correctionAttempts) {
    const proposal = proposals.find(item => item.proposalId === attempt.proposalId)
    const revision = revisions.find(item => item.revisionId === attempt.revisionId)
    if (!proposal || !revision || proposal.proposalFingerprint !== attempt.proposalFingerprint ||
        revision.correctionProposalId !== proposal.proposalId ||
        revision.correctionProposalFingerprint !== proposal.proposalFingerprint ||
        revision.previousRevisionId !== proposal.previousApprovedRevisionId) return false
  }

  const currentAnalysis = task.currentFailureAnalysisId === null ? null :
    analyses.find(item => item.analysisId === task.currentFailureAnalysisId) ?? null
  const currentProposal = task.currentCorrectionProposalId === null ? null :
    proposals.find(item => item.proposalId === task.currentCorrectionProposalId) ?? null
  const currentManualAttempt = manualAttempts
    .find(item => item.attemptId === task.currentVerificationAttemptId)
  const status = task.status as string
  const latestFailure = failureAttempts.at(-1)
  const latestCorrection = correctionAttempts.at(-1)
  const revision1 = revisions[0]
  const revision2 = revisions[1]
  const correctedRevisionIsEffective = revision2 !== undefined &&
    task.activeImplementationRevisionId === revision2.revisionId &&
    task.approvedImplementationRevisionId === revision2.revisionId &&
    revision1.reviewState === 'HistoricallyApproved' && revision2.reviewState === 'Approved'
  const correctionReviewWithHistoricalVerification = status === 'AwaitingImplementationReview' &&
    revisions.length === 2 && revision1.kind === 'Initial' && revision2.kind === 'Correction' &&
    revision1.reviewState === 'Approved' && revision1.isCurrent === false && revision1.isApproved === true &&
    revision2.reviewState === 'Current' && revision2.isCurrent === true && revision2.isApproved === false &&
    task.activeImplementationRevisionId === revision2.revisionId &&
    task.approvedImplementationRevisionId === revision1.revisionId && task.pendingImplementationRevisionId === null &&
    currentAnalysis !== null && currentProposal !== null && currentProposal.status === 'Approved' &&
    currentManualAttempt?.status === 'CompletedFailed' &&
    currentManualAttempt.verificationPlanId === task.currentVerificationPlanId &&
    currentManualAttempt.implementationRevisionId === revision1.revisionId &&
    plans.find(plan => plan.planId === task.currentVerificationPlanId)?.status === 'Completed' &&
    plans.find(plan => plan.planId === task.currentVerificationPlanId)?.implementationRevisionId === revision1.revisionId &&
    latestCorrection?.status === 'Completed' && latestCorrection.revisionId === revision2.revisionId &&
    latestCorrection.proposalId === currentProposal.proposalId &&
    revision2.correctionProposalId === currentProposal.proposalId &&
    revision2.correctionProposalFingerprint === currentProposal.proposalFingerprint
  if (currentAnalysis && !correctedRevisionIsEffective &&
      (!currentManualAttempt || currentAnalysis.failedAttemptId !== currentManualAttempt.attemptId ||
       currentAnalysis.failedAttemptFingerprint !== currentManualAttempt.attemptFingerprint ||
       currentAnalysis.verificationPlanId !== task.currentVerificationPlanId ||
       currentAnalysis.implementationRevisionId !== currentManualAttempt.implementationRevisionId)) return false
  if (currentAnalysis && correctedRevisionIsEffective) {
    const historicalPlan = plans.find(item => item.planId === currentAnalysis.verificationPlanId)
    const historicalAttempt = manualAttempts.find(item => item.attemptId === currentAnalysis.failedAttemptId)
    if (historicalPlan?.status !== 'Superseded' || historicalAttempt?.status !== 'CompletedFailed' ||
        historicalPlan.implementationRevisionId !== revision1.revisionId ||
        historicalAttempt.implementationRevisionId !== revision1.revisionId) return false
  }
  if (currentProposal && (!currentAnalysis || currentProposal.analysisId !== currentAnalysis.analysisId ||
      currentProposal.analysisFingerprint !== currentAnalysis.analysisFingerprint)) return false
  if (status === 'FailureAnalysisPending' && (!latestFailure ||
      !['Prepared', 'DispatchMayHaveStarted', 'ResponseReceived'].includes(latestFailure.status as string))) return false
  if (status === 'FailureAnalysisRecoveryRequired' && (!latestFailure?.recoveryRequired || latestFailure.retryEligible ||
      currentAnalysis !== null || currentProposal !== null)) return false
  if (status === 'AwaitingCorrectionApproval' && (!currentProposal || currentProposal.status !== 'AwaitingApproval')) return false
  if ((status === 'CorrectionApproved' || status === 'ImplementingCorrection' || status === 'CorrectionRecoveryRequired') &&
      (!currentProposal || currentProposal.status !== 'Approved')) return false
  if (status === 'ImplementingCorrection' && (!latestCorrection || latestCorrection.completedAt !== null ||
      latestCorrection.recoveryRequired || revision2 === undefined || task.activeImplementationRevisionId !== revision1.revisionId ||
      task.approvedImplementationRevisionId !== revision1.revisionId || task.pendingImplementationRevisionId !== revision2.revisionId)) return false
  if (status === 'CorrectionRecoveryRequired' && (!latestCorrection?.recoveryRequired || latestCorrection.retryEligible ||
      revision2 === undefined || task.activeImplementationRevisionId !== revision1.revisionId ||
      task.approvedImplementationRevisionId !== revision1.revisionId || task.pendingImplementationRevisionId !== revision2.revisionId)) return false
  if (status === 'AwaitingImplementationReview' && revision2 !== undefined &&
      !correctionReviewWithHistoricalVerification) return false
  if (['ImplementationApproved', 'VerificationPlanning', 'AwaitingManualVerification', 'ManualVerificationFailed', 'ReadyForDelivery'].includes(status) &&
      revision2 !== undefined && (task.activeImplementationRevisionId !== revision2.revisionId ||
        task.approvedImplementationRevisionId !== revision2.revisionId || revision1.reviewState !== 'HistoricallyApproved' ||
        revision2.reviewState !== 'Approved' || task.pendingImplementationRevisionId !== null)) return false
  if (status === 'ImplementationApproved' && revision2 !== undefined &&
      (task.currentVerificationPlanId !== null || task.currentVerificationAttemptId !== null)) return false

  const expected = {
    canGenerateFailureAnalysis: status === 'ManualVerificationFailed' && revisions.length === 1 && revision1.kind === 'Initial',
    canApproveCorrection: status === 'AwaitingCorrectionApproval',
    canGenerateCorrection: status === 'CorrectionApproved',
    canApproveCorrectedRevision: correctionReviewWithHistoricalVerification,
    canGenerateReplacementVerificationPlan: status === 'ImplementationApproved' && revisions.length === 2 &&
      task.currentVerificationPlanId === null,
  }
  if (!isRecord(task.correctionEligibility)) return false
  const eligibility = task.correctionEligibility
  return Object.entries(expected).every(([key, value]) => eligibility[key] === value)
}

function validateTaskRelationships(task: Record<string, unknown>, telemetry: ModelTelemetry) {
  const revisions = task.implementationRevisions as Array<Record<string, unknown>>
  const revisionIds = revisions.map(revision => revision.revisionId as string)
  if (!areUnique(revisionIds) || !isSequential(revisions.map(revision => revision.revisionNumber as number))) return false
  for (let index = 0; index < revisions.length; index += 1) {
    const revision = revisions[index]
    if (index === 0 && revision.previousRevisionId !== null || index > 0 &&
        revision.previousRevisionId !== revisions[index - 1].revisionId) return false
  }
  const currentRevisions = revisions.filter(revision => revision.isCurrent)
  const approvedRevisions = revisions.filter(revision => revision.isApproved)
  if (task.activeImplementationRevisionId === null ? currentRevisions.length !== 0 :
      currentRevisions.length !== 1 || currentRevisions[0].revisionId !== task.activeImplementationRevisionId) return false
  if (task.approvedImplementationRevisionId === null ? approvedRevisions.length !== 0 :
      approvedRevisions.length !== 1 || approvedRevisions[0].revisionId !== task.approvedImplementationRevisionId) return false
  if (task.implementationResult !== null) {
    const result = task.implementationResult as Record<string, unknown>
    if (currentRevisions.length !== 1 || currentRevisions[0].resultFingerprint === null ||
        currentRevisions[0].changedFileCount !== (result.changedFiles as unknown[]).length ||
        currentRevisions[0].baseCommitSha !== result.baseCommitSha ||
        currentRevisions[0].generationCompletedAt === null ||
        Date.parse(result.completedAt as string) < Date.parse(currentRevisions[0].generationStartedAt as string) ||
        Date.parse(result.completedAt as string) > Date.parse(currentRevisions[0].generationCompletedAt as string)) return false
  }

  const evidence = task.evidenceItems as Array<Record<string, unknown>>
  if (!areUnique(evidence.map(item => item.id as string))) return false
  const snapshot = task.repositorySnapshot as Record<string, unknown> | null
  if (snapshot && !areUnique((snapshot.files as Array<Record<string, unknown>>).map(file => file.relativePath as string))) return false

  const plans = task.verificationPlans as Array<Record<string, unknown>>
  const planIds = plans.map(plan => plan.planId as string)
  if (!areUnique(planIds) || !isSequential(plans.map(plan => plan.planNumber as number)) ||
      plans.some(plan => !revisionIds.includes(plan.implementationRevisionId as string)) ||
      plans.some((plan, index) => index > 0 && Date.parse(plan.generatedAt as string) <
        Date.parse(plans[index - 1].generatedAt as string))) return false
  if (task.currentVerificationPlanId === null ? false :
      plans.filter(plan => plan.planId === task.currentVerificationPlanId).length !== 1) return false

  const generations = task.verificationPlanGenerationAttempts as Array<Record<string, unknown>>
  if (!areUnique(generations.map(generation => generation.commandId as string)) ||
      generations.some((generation, index) => index > 0 && Date.parse(generation.startedAt as string) <
        Date.parse(generations[index - 1].startedAt as string))) return false
  const modelCallIds = telemetry.calls.map(call => call.id)
  if (!areUnique(modelCallIds)) return false
  for (const generation of generations) {
    if ((generation.modelCallIds as string[]).some(id => !modelCallIds.includes(id) ||
        telemetry.calls.find(call => call.id === id)?.stage !== 'VerificationPlanning') ||
        generation.resultPlanId !== null && !planIds.includes(generation.resultPlanId as string)) return false
  }
  if (telemetry.verificationLogicalAttemptCount !== generations.reduce((sum, item) =>
      sum + (item.logicalCallCount as number), 0) ||
      telemetry.verificationPhysicalRequestCount !== generations.reduce((sum, item) =>
        sum + (item.physicalRequestCount as number), 0) ||
      telemetry.verificationPossiblyDispatchedRequestCount !== generations.reduce((sum, item) =>
        sum + (item.possiblyDispatchedRequestCount as number), 0) ||
      telemetry.verificationDefinitelyUndispatchedAttemptCount !== telemetry.calls.filter(call =>
        call.stage === 'VerificationPlanning' && call.verificationDispatchDisposition === 'DefinitelyNotDispatched').length ||
      telemetry.possiblyDispatchedUnavailableEstimatedCostCallCount !== telemetry.calls.filter(call =>
        call.verificationDispatchDisposition === 'PossiblyDispatched' && call.estimatedCostUsd === null).length) return false

  const attempts = task.manualVerificationAttempts as Array<Record<string, unknown>>
  const attemptIds = attempts.map(attempt => attempt.attemptId as string)
  if (!areUnique(attemptIds) || !isSequential(attempts.map(attempt => attempt.attemptNumber as number))) return false
  if (task.currentVerificationAttemptId === null ? false :
      attempts.filter(attempt => attempt.attemptId === task.currentVerificationAttemptId).length !== 1) return false
  const resultIds: string[] = []
  for (const attempt of attempts) {
    const plan = plans.find(item => item.planId === attempt.verificationPlanId)
    if (!plan || plan.implementationRevisionId !== attempt.implementationRevisionId ||
        plan.implementationResultFingerprint !== attempt.implementationResultFingerprint ||
        plan.planFingerprint !== attempt.verificationPlanFingerprint) return false
    const testIds = (plan.testCases as Array<Record<string, unknown>>).map(testCase => testCase.testCaseId as string)
    const revisionsForAttempt = attempt.resultRevisions as Array<Record<string, unknown>>
    const currentResults = attempt.currentCaseResults as Array<Record<string, unknown>>
    resultIds.push(...revisionsForAttempt.map(result => result.resultRevisionId as string))
    if (revisionsForAttempt.some(result => !testIds.includes(result.testCaseId as string)) ||
        revisionsForAttempt.some(result => Date.parse(result.recordedAt as string) < Date.parse(attempt.startedAt as string)) ||
        currentResults.some(result => !testIds.includes(result.testCaseId as string) ||
          !revisionsForAttempt.some(stored => stored.resultRevisionId === result.resultRevisionId)) ||
        !areUnique(currentResults.map(result => result.testCaseId as string))) return false
    for (const testId of testIds) {
      const history = revisionsForAttempt.filter(result => result.testCaseId === testId)
        .sort((left, right) => (left.revisionNumber as number) - (right.revisionNumber as number))
      if (!isSequential(history.map(result => result.revisionNumber as number)) || history.some((result, index) =>
          index === 0 ? result.supersedesResultRevisionId !== null :
            result.supersedesResultRevisionId !== history[index - 1].resultRevisionId)) return false
      const projected = currentResults.filter(result => result.testCaseId === testId)
      if (history.length === 0 ? projected.length !== 0 :
          projected.length !== 1 || projected[0].resultRevisionId !== history.at(-1)?.resultRevisionId) return false
    }
    if (attempt.status === 'InProgress') {
      if (attempt.completedAt !== null || attempt.completionConfirmation !== null ||
          attempt.attemptFingerprint !== null || attempt.passedAt !== null || attempt.failedAt !== null) return false
    } else if (attempt.completedAt === null || attempt.completionConfirmation !== true ||
        attempt.attemptFingerprint === null || (attempt.status === 'CompletedPassed'
          ? attempt.passedAt === null || attempt.failedAt !== null
          : attempt.failedAt === null || attempt.passedAt !== null)) return false
  }
  if (!areUnique(resultIds)) return false
  if (!validateCorrectionRelationships(task, telemetry)) return false
  if (!validateDeliveryRelationships(task)) return false

  const currentPlan = task.currentVerificationPlanId === null ? null :
    plans.find(plan => plan.planId === task.currentVerificationPlanId) ?? null
  const currentAttempt = task.currentVerificationAttemptId === null ? null :
    attempts.find(attempt => attempt.attemptId === task.currentVerificationAttemptId) ?? null
  const eligibility = task.verificationEligibility as Record<string, unknown>
  const manualFlags = [eligibility.canStartVerificationAttempt, eligibility.canRecordVerificationResult,
    eligibility.canCompleteVerificationPassed, eligibility.canCompleteVerificationFailed]
  const noManualActions = manualFlags.every(flag => flag === false)
  const currentBindingIsValid = currentPlan !== null && currentAttempt !== null &&
    currentAttempt.verificationPlanId === currentPlan.planId &&
    currentAttempt.verificationPlanFingerprint === currentPlan.planFingerprint &&
    currentAttempt.implementationRevisionId === currentPlan.implementationRevisionId &&
    currentAttempt.implementationRevisionId === task.approvedImplementationRevisionId &&
    currentAttempt.implementationResultFingerprint === currentPlan.implementationResultFingerprint

  if (task.status === 'AwaitingManualVerification') {
    if (currentPlan === null || currentPlan.status !== 'Current') return false
    if (currentAttempt === null) return !attempts.some(attempt => attempt.verificationPlanId === currentPlan.planId) &&
      eligibility.canRecordVerificationResult === false &&
      eligibility.canCompleteVerificationPassed === false && eligibility.canCompleteVerificationFailed === false &&
      eligibility.readyForDelivery === false
    return currentBindingIsValid && currentAttempt.status === 'InProgress' &&
      eligibility.canStartVerificationAttempt === false && eligibility.readyForDelivery === false &&
      eligibility.canCompleteVerificationPassed === canCompleteManualAttempt(currentPlan, currentAttempt, true) &&
      eligibility.canCompleteVerificationFailed === canCompleteManualAttempt(currentPlan, currentAttempt, false)
  }
  if (task.status === 'ReadyForDelivery') return currentBindingIsValid &&
    currentPlan?.status === 'Completed' && currentAttempt?.status === 'CompletedPassed' &&
    noManualActions && eligibility.readyForDelivery === true
  if (task.status === 'ManualVerificationFailed') return currentBindingIsValid &&
    currentPlan?.status === 'Completed' && currentAttempt?.status === 'CompletedFailed' &&
    noManualActions && eligibility.readyForDelivery === false
  return noManualActions && eligibility.readyForDelivery === false
}

export function decodeEngineeringTask(value: unknown): EngineeringTask {
  if (!isRecord(value)) throw new ForgeApiError('The task response could not be validated safely.')
  const fail = (): never => { throw new ForgeApiError('The task response could not be validated safely.') }
  if (!isGuid(value.id) || !isSafeString(value.repository, 1_000) ||
      !isSafeString(value.originalRequirement, 100_000) || !isSafeString(value.currentClarifiedRequirement, 100_000) ||
      !Array.isArray(value.clarificationAnswers) || !value.clarificationAnswers.every(decodeClarificationAnswer) ||
      !Array.isArray(value.requirementRevisionNotes) || !value.requirementRevisionNotes.every(decodeRequirementRevision) ||
      !Array.isArray(value.planRevisionNotes) || !value.planRevisionNotes.every(decodePlanRevision) ||
      !isNullableString(value.currentPendingQuestion, 20_000) || !isNullableString(value.requirementSummary, 100_000) ||
      typeof value.status !== 'string' || !workflowStatuses.has(value.status) || !isTimestamp(value.createdAt) ||
      !isTimestamp(value.updatedAt) || Date.parse(value.updatedAt as string) < Date.parse(value.createdAt as string) ||
      !isNullableTimestamp(value.requirementApprovedAt) || !isNullableTimestamp(value.planApprovedAt) ||
      !decodeRepositorySnapshot(value.repositorySnapshot) || !Array.isArray(value.evidenceItems) ||
      value.evidenceItems.length > 10_000 || !value.evidenceItems.every(decodeEvidenceItem) ||
      !isSafeInteger(value.evidenceFilesInspected) || !isSafeInteger(value.evidenceFilesSelected) ||
      value.evidenceFilesSelected > value.evidenceFilesInspected || !isSafeInteger(value.totalEvidenceCharacters) ||
      !decodeImplementationPlan(value.implementationPlan) || !isNullableTimestamp(value.repositoryAnalyzedAt) ||
      !isNullableString(value.repositoryFingerprint, 256) || !isNullableTimestamp(value.planCreatedAt) ||
      !decodeImplementationWorkspace(value.implementationWorkspace) || !decodeImplementationResult(value.implementationResult) ||
      !decodeImplementationFailure(value.lastImplementationFailure) || !isNullableTimestamp(value.implementationStartedAt) ||
      !isNullableTimestamp(value.implementationCompletedAt) || !decodeImplementationRuntime(value.implementationRuntime) ||
      !isSafeInteger(value.rowVersion) || value.rowVersion < 1 || !isNullableGuid(value.activeImplementationRevisionId) ||
      !isNullableGuid(value.approvedImplementationRevisionId) || !Array.isArray(value.implementationRevisions) ||
      value.implementationRevisions.length > 100 || !value.implementationRevisions.every(decodeImplementationRevision) ||
      !Array.isArray(value.verificationPlans) || value.verificationPlans.length > 100 ||
      !value.verificationPlans.every(decodeVerificationPlan) || !Array.isArray(value.verificationPlanGenerationAttempts) ||
      value.verificationPlanGenerationAttempts.length > 100 ||
      !value.verificationPlanGenerationAttempts.every(decodeGenerationAttempt) ||
      !Array.isArray(value.manualVerificationAttempts) || value.manualVerificationAttempts.length > 100 ||
      !value.manualVerificationAttempts.every(decodeManualAttempt) || !isNullableGuid(value.currentVerificationPlanId) ||
      !isNullableGuid(value.currentVerificationAttemptId) ||
      !(value.pendingImplementationRevisionId === undefined || isNullableGuid(value.pendingImplementationRevisionId)) ||
      !(value.currentFailureAnalysisId === undefined || isNullableGuid(value.currentFailureAnalysisId)) ||
      !(value.currentCorrectionProposalId === undefined || isNullableGuid(value.currentCorrectionProposalId)) ||
      !(value.failureAnalyses === undefined || Array.isArray(value.failureAnalyses) && value.failureAnalyses.length <= 6 &&
        value.failureAnalyses.every(decodeFailureAnalysis)) ||
      !(value.correctionProposals === undefined || Array.isArray(value.correctionProposals) &&
        value.correctionProposals.length <= 1 && value.correctionProposals.every(decodeCorrectionProposal)) ||
      !(value.failureAnalysisGenerationAttempts === undefined || Array.isArray(value.failureAnalysisGenerationAttempts) &&
        value.failureAnalysisGenerationAttempts.length <= 6 && value.failureAnalysisGenerationAttempts.every(decodeFailureAnalysisAttempt)) ||
      !(value.correctionGenerationAttempts === undefined || Array.isArray(value.correctionGenerationAttempts) &&
        value.correctionGenerationAttempts.length <= 6 && value.correctionGenerationAttempts.every(decodeCorrectionGenerationAttempt)) ||
      !(value.correctionApprovalAudit === undefined || Array.isArray(value.correctionApprovalAudit) &&
        value.correctionApprovalAudit.length <= 1 && value.correctionApprovalAudit.every(decodeCorrectionApprovalAudit)) ||
      !(value.correctionEligibility === undefined || decodeCorrectionEligibility(value.correctionEligibility)) ||
      !(value.currentDeliveryProposalId === undefined || isNullableGuid(value.currentDeliveryProposalId)) ||
      !(value.currentDeliveryAttemptId === undefined || isNullableGuid(value.currentDeliveryAttemptId)) ||
      !(value.deliveryProposals === undefined || Array.isArray(value.deliveryProposals) &&
        value.deliveryProposals.length <= 1 && value.deliveryProposals.every(decodeDeliveryProposal)) ||
      !(value.deliveryAttempts === undefined || Array.isArray(value.deliveryAttempts) &&
        value.deliveryAttempts.length <= 3 && value.deliveryAttempts.every(decodeDeliveryAttempt)) ||
      !(value.deliveryEligibility === undefined || decodeDeliveryEligibility(value.deliveryEligibility))) fail()
  const telemetry = decodeTelemetry(value.telemetry)
  const eligibility = normalizeVerificationEligibility(value)
  if (!telemetry || !eligibility || !validateTaskRelationships(value, telemetry))
    throw new ForgeApiError('The task response could not be validated safely.')
  const decoded = value as typeof value & EngineeringTask
  return {
    id: decoded.id, repository: decoded.repository, originalRequirement: decoded.originalRequirement,
    currentClarifiedRequirement: decoded.currentClarifiedRequirement,
    clarificationAnswers: decoded.clarificationAnswers,
    requirementRevisionNotes: decoded.requirementRevisionNotes,
    planRevisionNotes: decoded.planRevisionNotes,
    currentPendingQuestion: decoded.currentPendingQuestion, requirementSummary: decoded.requirementSummary,
    status: decoded.status, createdAt: decoded.createdAt, updatedAt: decoded.updatedAt,
    requirementApprovedAt: decoded.requirementApprovedAt, planApprovedAt: decoded.planApprovedAt,
    repositorySnapshot: decoded.repositorySnapshot, evidenceItems: decoded.evidenceItems,
    evidenceFilesInspected: decoded.evidenceFilesInspected, evidenceFilesSelected: decoded.evidenceFilesSelected,
    totalEvidenceCharacters: decoded.totalEvidenceCharacters, implementationPlan: decoded.implementationPlan,
    repositoryAnalyzedAt: decoded.repositoryAnalyzedAt, repositoryFingerprint: decoded.repositoryFingerprint,
    planCreatedAt: decoded.planCreatedAt, implementationWorkspace: decoded.implementationWorkspace,
    implementationResult: decoded.implementationResult, lastImplementationFailure: decoded.lastImplementationFailure,
    implementationStartedAt: decoded.implementationStartedAt, implementationCompletedAt: decoded.implementationCompletedAt,
    implementationRuntime: decoded.implementationRuntime, rowVersion: decoded.rowVersion,
    activeImplementationRevisionId: decoded.activeImplementationRevisionId,
    approvedImplementationRevisionId: decoded.approvedImplementationRevisionId,
    pendingImplementationRevisionId: decoded.pendingImplementationRevisionId ?? null,
    implementationRevisions: decoded.implementationRevisions, telemetry,
    currentVerificationPlanId: decoded.currentVerificationPlanId,
    currentVerificationAttemptId: decoded.currentVerificationAttemptId,
    verificationPlans: decoded.verificationPlans,
    verificationPlanGenerationAttempts: decoded.verificationPlanGenerationAttempts,
    manualVerificationAttempts: decoded.manualVerificationAttempts,
    verificationEligibility: eligibility as typeof eligibility & EngineeringTask['verificationEligibility'],
    currentFailureAnalysisId: decoded.currentFailureAnalysisId ?? null,
    currentCorrectionProposalId: decoded.currentCorrectionProposalId ?? null,
    failureAnalyses: decoded.failureAnalyses ?? [],
    correctionProposals: decoded.correctionProposals ?? [],
    failureAnalysisGenerationAttempts: decoded.failureAnalysisGenerationAttempts ?? [],
    correctionGenerationAttempts: decoded.correctionGenerationAttempts ?? [],
    correctionApprovalAudit: decoded.correctionApprovalAudit ?? [],
    correctionEligibility: decoded.correctionEligibility ?? {
      canGenerateFailureAnalysis: false, canApproveCorrection: false, canGenerateCorrection: false,
      canApproveCorrectedRevision: false, canGenerateReplacementVerificationPlan: false,
    },
    currentDeliveryProposalId: decoded.currentDeliveryProposalId ?? null,
    currentDeliveryAttemptId: decoded.currentDeliveryAttemptId ?? null,
    deliveryProposals: decoded.deliveryProposals ?? [],
    deliveryAttempts: decoded.deliveryAttempts ?? [],
    deliveryEligibility: decoded.deliveryEligibility ?? {
      canPrepareDelivery: decoded.status === 'ReadyForDelivery', canApproveDelivery: false,
      canExecuteDelivery: false, canReconcileDelivery: false, deliveryRecoveryRequired: false, pullRequestCreated: false,
    },
  }
}

export function decodeEngineeringTaskSummaries(value: unknown): EngineeringTaskSummary[] {
  if (!Array.isArray(value) || value.some(item => !isRecord(item) || !isGuid(item.id) ||
      !workflowStatuses.has(item.status as string) || !isTimestamp(item.createdAt) || !isTimestamp(item.updatedAt) ||
      !isSafeString(item.repository, 500) || !isSafeString(item.originalRequirementPreview, 500) ||
      !(item.readyForDelivery === undefined || typeof item.readyForDelivery === 'boolean') ||
      !(item.verificationStatus === undefined || isNullableString(item.verificationStatus, 100)) ||
      !(item.verificationProgressSummary === undefined || isNullableString(item.verificationProgressSummary, 500))))
    throw new ForgeApiError('The task-history response could not be validated safely.')
  return value as EngineeringTaskSummary[]
}

async function taskRequest(url: string, init?: RequestInit): Promise<EngineeringTask> {
  const task = decodeEngineeringTask(await request<unknown>(url, init))
  const expectedTaskId = /^\/api\/tasks\/([^/]+)/.exec(url)?.[1]
  if (expectedTaskId && isGuid(expectedTaskId) && task.id !== expectedTaskId)
    throw new ForgeApiError('The task response could not be validated safely.')
  return task
}

export function decodeSystemCapabilities(value: unknown): SystemCapabilities {
  const stringFields = ['aiMode', 'clarificationProvider', 'clarificationModel', 'reasoningEffort',
    'planningProvider', 'planningModel', 'planningReasoningEffort', 'implementationProvider',
    'verificationPlanningProvider', 'verificationPlanningModel', 'verificationPlanningReasoningEffort']
  const booleanFields = ['clarificationConfigured', 'planningConfigured', 'implementationConfigured', 'aiConfigured',
    'repositoryInspectionAvailable', 'planningAvailable', 'targetModificationAvailable',
    'implementationApprovalAvailable', 'implementationCorrectionAvailable', 'validationAvailable', 'reviewAvailable',
    'pullRequestCreationAvailable', 'fakeImplementationAvailable', 'openAiImplementationAvailable',
    'silentFallbackSupported', 'commitAvailable', 'pushAvailable', 'deliveryPullRequestAvailable',
    'verificationPlanningConfigured', 'manualResultRecordingAvailable', 'automatedValidationAvailable',
    'failureAnalysisAvailable', 'failureAnalysisConfigured', 'implementationCorrectionConfigured',
    'deliveryPullRequestConfigured', 'autoMergeAvailable', 'deliveryGitAvailable', 'deliveryGitHubCliAvailable']
  if (!isRecord(value) || stringFields.some(field => !isSafeString(value[field], 200)) ||
      booleanFields.some(field => typeof value[field] !== 'boolean') ||
      !(value.implementationModel === null || isSafeString(value.implementationModel, 200)) ||
      !(value.implementationReasoningEffort === null || isSafeString(value.implementationReasoningEffort, 50)))
    throw new ForgeApiError('The system-capabilities response could not be validated safely.')
  const deliveryConfigured = value.deliveryGitAvailable === true && value.deliveryGitHubCliAvailable === true
  if (value.deliveryPullRequestConfigured !== deliveryConfigured || value.commitAvailable !== deliveryConfigured ||
      value.pushAvailable !== deliveryConfigured || value.deliveryPullRequestAvailable !== deliveryConfigured ||
      value.pullRequestCreationAvailable !== deliveryConfigured || value.automatedValidationAvailable !== false ||
      value.autoMergeAvailable !== false)
    throw new ForgeApiError('The system-capabilities response could not be validated safely.')
  return value as unknown as SystemCapabilities
}

async function throwResponseError(response: Response): Promise<never> {
  let problem: ProblemDetails | undefined
  try { problem = (await response.json()) as ProblemDetails } catch { /* Use the status fallback below. */ }
  const validation = problem?.errors ? Object.values(problem.errors).flat().join(' ') : undefined
  throw new ForgeApiError(validation || problem?.detail || problem?.title || `Request failed (${response.status}).`, problem?.code)
}

export interface TaskPdfDownload { blob: Blob; filename: string }

export function parseSafePdfFilename(contentDisposition: string | null, taskId: string, documentType: 'task' | 'plan' = 'task') {
  const fallback = `forge-${documentType}-${taskId}.pdf`
  if (!contentDisposition) return fallback
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(contentDisposition)?.[1]
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(contentDisposition)?.[1]
  let candidate = encoded ?? plain
  if (!candidate) return fallback
  try { candidate = decodeURIComponent(candidate.trim()) } catch { return fallback }
  return /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.pdf$/i.test(candidate) ? candidate : fallback
}

export const forgeApi = {
  listTasks: async () => decodeEngineeringTaskSummaries(await request<unknown>('/api/tasks')),
  getTask: (id: string) => taskRequest(`/api/tasks/${id}`),
  createTask: (repository: string, requirement: string) => taskRequest('/api/tasks', { method: 'POST', body: JSON.stringify({ repository, requirement }) }),
  answerQuestion: (id: string, answer: string) => taskRequest(`/api/tasks/${id}/answers`, { method: 'POST', body: JSON.stringify({ answer }) }),
  requestRevision: (id: string, correction: string) => taskRequest(`/api/tasks/${id}/requirement-revision`, { method: 'POST', body: JSON.stringify({ correction }) }),
  approveRequirement: (id: string) => taskRequest(`/api/tasks/${id}/requirement-approval`, { method: 'POST' }),
  analyzeRepository: (id: string) => taskRequest(`/api/tasks/${id}/repository-analysis`, { method: 'POST' }),
  refreshEvidence: (id: string) => taskRequest(`/api/tasks/${id}/evidence-refresh`, { method: 'POST' }),
  createPlan: (id: string) => taskRequest(`/api/tasks/${id}/plan`, { method: 'POST' }),
  requestPlanRevision: (id: string, correction: string) => taskRequest(`/api/tasks/${id}/plan-revision`, { method: 'POST', body: JSON.stringify({ correction }) }),
  approvePlan: (id: string) => taskRequest(`/api/tasks/${id}/plan-approval`, { method: 'POST' }),
  generateImplementation: (id: string) => taskRequest(`/api/tasks/${id}/implementation`, { method: 'POST' }),
  approveImplementation: (id: string, payload: { commandId: string; expectedRowVersion: number; expectedRevisionId: string; expectedResultFingerprint: string }) =>
    taskRequest(`/api/tasks/${id}/implementation-approval`, { method: 'POST', body: JSON.stringify(payload) }),
  generateFailureAnalysis: (id: string, payload: { commandId: string; expectedRowVersion: number; expectedFailedAttemptId: string; expectedFailedAttemptFingerprint: string }) =>
    taskRequest(`/api/tasks/${id}/failure-analysis`, { method: 'POST', body: JSON.stringify(payload) }),
  reconcileFailureAnalysis: (id: string, attemptId: string, payload: { commandId: string; expectedRowVersion: number }) =>
    taskRequest(`/api/tasks/${id}/failure-analysis-attempts/${attemptId}/reconcile`, { method: 'POST', body: JSON.stringify(payload) }),
  approveCorrectionProposal: (id: string, proposalId: string, payload: Record<string, unknown>) =>
    taskRequest(`/api/tasks/${id}/correction-proposals/${proposalId}/approve`, { method: 'POST', body: JSON.stringify(payload) }),
  generateImplementationCorrection: (id: string, payload: { commandId: string; expectedRowVersion: number; proposalId: string; proposalFingerprint: string; previousRevisionId: string; previousResultFingerprint: string }) =>
    taskRequest(`/api/tasks/${id}/implementation-corrections`, { method: 'POST', body: JSON.stringify(payload) }),
  reconcileImplementationCorrection: (id: string, attemptId: string, payload: { commandId: string; expectedRowVersion: number; proposalId: string; proposalFingerprint: string; previousRevisionId: string; previousResultFingerprint: string; revisionId: string }) =>
    taskRequest(`/api/tasks/${id}/implementation-corrections/${attemptId}/reconcile`, { method: 'POST', body: JSON.stringify(payload) }),
  generateVerificationPlan: (id: string, payload: { commandId: string; expectedRowVersion: number; expectedImplementationRevisionId: string; expectedImplementationResultFingerprint: string }) =>
    taskRequest(`/api/tasks/${id}/verification-plans`, { method: 'POST', body: JSON.stringify(payload) }),
  startVerificationAttempt: (id: string, payload: { commandId: string; expectedRowVersion: number; expectedVerificationPlanId: string; expectedVerificationPlanFingerprint: string; expectedImplementationRevisionId: string; expectedImplementationResultFingerprint: string }) =>
    taskRequest(`/api/tasks/${id}/verification-attempts`, { method: 'POST', body: JSON.stringify(payload) }),
  updateVerificationCase: (id: string, attemptId: string, caseId: string, payload: { commandId: string; expectedRowVersion: number; expectedVerificationPlanId: string; expectedVerificationPlanFingerprint: string; expectedImplementationRevisionId: string; expectedImplementationResultFingerprint: string; result: VerificationCaseResult; notes: string | null; actualResult: string | null; evidenceDescriptions: string[]; notApplicableReason: string | null; failureDetails: VerificationFailureDetails | null }) =>
    taskRequest(`/api/tasks/${id}/verification-attempts/${attemptId}/cases/${caseId}`, { method: 'PUT', body: JSON.stringify(payload) }),
  completeVerification: (id: string, attemptId: string, passed: boolean, payload: { commandId: string; expectedRowVersion: number; expectedVerificationPlanId: string; expectedVerificationPlanFingerprint: string; expectedImplementationRevisionId: string; expectedImplementationResultFingerprint: string; confirmedByHuman: boolean; summary: string | null }) =>
    taskRequest(`/api/tasks/${id}/verification-attempts/${attemptId}/${passed ? 'complete-passed' : 'complete-failed'}`, { method: 'POST', body: JSON.stringify(payload) }),
  prepareDelivery: (id: string, payload: Record<string, unknown>) =>
    taskRequest(`/api/tasks/${id}/delivery-proposals`, { method: 'POST', body: JSON.stringify(payload) }),
  approveDelivery: (id: string, proposalId: string, payload: Record<string, unknown>) =>
    taskRequest(`/api/tasks/${id}/delivery-proposals/${proposalId}/approve`, { method: 'POST', body: JSON.stringify(payload) }),
  executeDelivery: (id: string, payload: Record<string, unknown>) =>
    taskRequest(`/api/tasks/${id}/deliveries`, { method: 'POST', body: JSON.stringify(payload) }),
  reconcileDelivery: (id: string, attemptId: string, payload: Record<string, unknown>) =>
    taskRequest(`/api/tasks/${id}/delivery-attempts/${attemptId}/reconcile`, { method: 'POST', body: JSON.stringify(payload) }),
  exportTaskPdf: async (id: string): Promise<TaskPdfDownload> => {
    const response = await fetch(`/api/tasks/${id}/export/pdf`, { headers: { Accept: 'application/pdf' } })
    if (!response.ok) await throwResponseError(response)
    return {
      blob: await response.blob(),
      filename: parseSafePdfFilename(response.headers.get('Content-Disposition'), id),
    }
  },
  exportPlanPdf: async (id: string): Promise<TaskPdfDownload> => {
    const response = await fetch(`/api/tasks/${id}/export/plan-pdf`, { headers: { Accept: 'application/pdf' } })
    if (!response.ok) await throwResponseError(response)
    return {
      blob: await response.blob(),
      filename: parseSafePdfFilename(response.headers.get('Content-Disposition'), id, 'plan'),
    }
  },
  exportVerificationPlanPdf: async (id: string, planId: string): Promise<TaskPdfDownload> => {
    const response = await fetch(`/api/tasks/${id}/verification-plans/${planId}/export/pdf`, { headers: { Accept: 'application/pdf' } })
    if (!response.ok) await throwResponseError(response)
    return { blob: await response.blob(), filename: parseSafePdfFilename(response.headers.get('Content-Disposition'), planId, 'plan') }
  },
  getCapabilities: async () => decodeSystemCapabilities(await request<unknown>('/api/system/capabilities')),
}
