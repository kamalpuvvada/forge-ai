export type WorkflowStatus = 'Draft' | 'Clarifying' | 'RequirementSummaryReady' | 'AwaitingRequirementApproval' | 'ReadyForPlanning' | 'Planning' | 'AwaitingPlanApproval' | 'PlanApproved' | 'Implementing' | 'AwaitingImplementationReview' | 'ImplementationApproved' | 'VerificationPlanning' | 'AwaitingManualVerification' | 'ManualVerificationFailed' | 'FailureAnalysisPending' | 'FailureAnalysisRecoveryRequired' | 'AwaitingFailureResolution' | 'AwaitingCorrectionApproval' | 'CorrectionApproved' | 'ImplementingCorrection' | 'CorrectionRecoveryRequired' | 'ReadyForDelivery' | 'AwaitingDeliveryApproval' | 'Delivering' | 'PullRequestCreated' | 'DeliveryRecoveryRequired' | 'Validating' | 'Reviewing' | 'Completed' | 'Failed'
export interface ClarificationAnswer { question: string; answer: string; answeredAt: string }
export interface RequirementRevision { correction: string; previousSummary: string; submittedAt: string }
export interface PlanRevision {
  correction: string
  submittedAt: string
  previousPlanTitle: string
  previousRepositoryFingerprint: string
  previousAffectedPaths: string[]
}
export interface ModelCall {
  id: string
  stage: string
  provider: string
  model: string
  reasoningEffort: string
  startedAt: string
  completedAt: string
  succeeded: boolean
  providerResponseId: string | null
  usageAvailable?: boolean
  inputTokens: number | null
  cachedInputTokens: number | null
  uncachedInputTokens: number | null
  outputTokens: number | null
  reasoningTokens: number | null
  estimatedCostUsd: number | null
  pricingProvenance: string
  hasStoredPricingSnapshot: boolean
  storedPricingSnapshot: { inputPerMillionUsd: number; cachedInputPerMillionUsd: number; outputPerMillionUsd: number } | null
  failureCategory: string | null
  providerRequestId: string | null
  providerUsageAvailability?: 'Complete' | 'Partial' | 'Unavailable' | null
  providerUsageAvailable?: boolean | null
  verificationDispatchDisposition?: 'DefinitelyNotDispatched' | 'PossiblyDispatched' | 'ResponseReceived' | null
  providerHttpStatusCode?: number | null
  isPartialEstimate?: boolean
}
export interface ModelTelemetry {
  totalCalls: number
  usageAvailability: 'Complete' | 'Partial' | 'Unavailable'
  usageUnavailableCallCount: number
  totalInputTokens: number | null
  totalCachedInputTokens: number | null
  totalOutputTokens: number | null
  totalReasoningTokens: number | null
  totalEstimatedCostUsd: number | null
  costUnavailableCallCount: number
  isPartialEstimate: boolean
  verificationLogicalAttemptCount?: number
  verificationPhysicalRequestCount?: number
  verificationPossiblyDispatchedRequestCount?: number
  verificationDefinitelyUndispatchedAttemptCount?: number
  completeEstimatedSubtotalUsd?: number | null
  partialEstimatedSubtotalUsd?: number | null
  availableEstimatedSubtotalUsd?: number | null
  hasPartialEstimates?: boolean
  possiblyDispatchedUnavailableEstimatedCostCallCount?: number
  calls: ModelCall[]
}
export interface RepositoryFile { relativePath: string; extension: string; sizeBytes: number; lineCount: number; probableRole: string; isTest: boolean; association: string | null; declaredSymbols: string[] }
export interface RepositorySnapshot {
  isGitRepository: boolean; branch: string | null; shortHeadSha: string | null; fullHeadSha: string | null
  workingTreeStatus: string; totalDiscoveredFiles: number; eligibleTextFileCount: number; excludedFileCount: number
  detectedLanguages: string[]; detectedExtensions: string[]; projectFiles: string[]; testLocations: string[]; warnings: string[]
  analyzedAt: string; fingerprint: string; files: RepositoryFile[]
}
export interface EvidenceItem { id: string; relativePath: string; startLine: number; endLine: number; excerpt: string; reasonSelected: string; score: number; contentHash: string }
export type PlannedFileAction = 'Modify' | 'Create' | 'Delete' | 'Inspect'
export interface PlannedFile { path: string; action: PlannedFileAction; purpose: string; evidenceIds: string[]; confidence: number }
export interface ImplementationStep { order: number; description: string; affectedPaths: string[]; evidenceIds: string[]; expectedResult: string }
export interface RequirementCoverage { requirement: string; affectedPaths: string[]; stepOrders: number[] }
export interface ImplementationPlan {
  title: string; objective: string; repositoryUnderstanding: string; affectedFiles: PlannedFile[]; orderedSteps: ImplementationStep[]
  proposedValidationCommands: string[]; risks: string[]; assumptions: string[]; unresolvedQuestions: string[]; requirementCoverage: RequirementCoverage[]; summary: string
  source: 'DeterministicFake' | 'OpenAI'; planningModel: string | null; isDeterministicFake: boolean; createdAt: string; repositoryFingerprint: string
}
export type ImplementationOperationAction = 'Create' | 'Modify' | 'Delete'
export interface ImplementationWorkspace {
  branch: string; baseCommitSha: string; phase: 'Reserved' | 'Ready' | 'RecoveryRequired' | 'Completed' | 'WorkspacePreparing' | 'WorkspacePrepared' | 'MutationStarted' | 'ApplyCompleted' | 'ResultPersisted' | 'Interrupted'
  createdAt: string; updatedAt: string; isAvailable: boolean
}
export type ImplementationAttemptDisposition = 'None' | 'Active' | 'SafeResume' | 'RecoveryRequired' | 'Interrupted' | 'TerminalIncompatible' | 'Completed'
export interface ImplementationRuntime { workspaceAvailable: boolean; activeCheckoutVerified: boolean; disposition: ImplementationAttemptDisposition; safeMessage: string | null }
export interface ImplementationFailure { category: string; message: string; recoveryRequired: boolean; occurredAt: string; safeToResume: boolean; activeCheckoutVerified: boolean }
export interface ChangedFileReview {
  path: string; action: ImplementationOperationAction; originalContentSha256: string | null; newContentSha256: string | null
  originalBytes: number; newBytes: number; originalLines: number; newLines: number; additions: number; deletions: number
  diffPreview: string; fullDiffCharacters: number; displayedDiffCharacters: number; diffTruncated: boolean; fullDiffUtf8Bytes: number; displayedDiffUtf8Bytes: number
}
export interface ImplementationResult {
  source: 'DeterministicFake' | 'OpenAI'; model: string | null; baseCommitSha: string; branch: string; summary: string
  warnings: string[]; changedFiles: ChangedFileReview[]; fullDiffCharacters: number; displayedDiffCharacters: number
  diffTruncated: boolean; completedAt: string; isDeterministicFake: boolean; fullDiffUtf8Bytes: number; displayedDiffUtf8Bytes: number; activeCheckoutVerified: boolean
}
export interface ImplementationRevision {
  revisionId: string; revisionNumber: number; kind: 'Initial' | 'Correction'; previousRevisionId: string | null
  planFingerprint: string; baseCommitSha: string; generationStartedAt: string; generationCompletedAt: string | null
  generationState: 'Requested' | 'Generating' | 'Succeeded' | 'Failed'; reviewState: 'NotReviewable' | 'Current' | 'Superseded' | 'Approved' | 'HistoricallyApproved'
  failureCategory: string | null; failureMessage: string | null; resultFingerprint: string | null; changedFileCount: number
  correctionSubmittedAt: string | null; approvedAt: string | null; isCurrent: boolean; isApproved: boolean
  correctionProposalId?: string | null; correctionProposalFingerprint?: string | null
}
export type VerificationCaseResult = 'NotStarted' | 'Passed' | 'Failed' | 'Blocked' | 'NotApplicable'
export interface VerificationTestStep { order: number; instruction: string; approvedValidationCommandId: string | null; expectedObservation: string }
export interface VerificationTestCase {
  testCaseId: string; order: number; title: string; objective: string; category: string; isRequired: boolean
  preconditions: string[]; testData: string[]; orderedSteps: VerificationTestStep[]; expectedResult: string
  negativeOrEdgeCases: string[]; regressionScope: string[]; evidenceRequirements: string[]; safetyNotes: string[]
  originTestCaseId?: string | null; regressionFailureReportIds?: string[]
}
export interface VerificationPlan {
  planId: string; planNumber: number; implementationRevisionId: string; implementationResultFingerprint: string
  approvedRequirementFingerprint: string; approvedPlanFingerprint: string; generationContextFingerprint: string
  generatedAt: string; source: 'DeterministicFake' | 'OpenAI'; model: string | null; reasoningEffort: string | null
  summary: string; scope: string; preconditions: string[]; testCases: VerificationTestCase[]; risks: string[]
  limitations: string[]; evidenceGuidance: string[]; planFingerprint: string; status: 'Current' | 'Superseded' | 'Completed'
  trustLabel: string; executionLabel: string
  supersedesPlanId?: string | null; regenerationReason?: string | null
}
export type FailureClassification = 'ImplementationDefect' | 'ApprovedPlanDefect' | 'ApprovedRequirementDefect' | 'EnvironmentOrSetupIssue' | 'InsufficientEvidence'
export interface ApprovedOperationReference { path: string; action: ImplementationOperationAction }
export interface FailureAnalysis {
  analysisId: string; analysisNumber: number; generationCommandId: string; contextFingerprint: string
  failedAttemptId: string; failedAttemptFingerprint: string; verificationPlanId: string; verificationPlanFingerprint: string
  implementationRevisionId: string; implementationResultFingerprint: string
  classification: FailureClassification; confidencePercent: number
  rootCauseSummary: string; rationale: string; evidenceReferences: string[]; affectedApprovedOperations: ApprovedOperationReference[]
  correctionStrategy: string; expectedBehavior: string; verificationImpact: string; risks: string[]
  source: 'DeterministicFake' | 'OpenAI'; model: string | null; reasoningEffort: string | null; modelCallIds: string[]
  analysisFingerprint: string; status: string; createdAt: string; trustLabel: string; safeRoute: string
}
export interface CorrectionProposal {
  proposalId: string; proposalNumber: number; analysisId: string; analysisFingerprint: string
  failedAttemptId: string; failedAttemptFingerprint: string; previousApprovedRevisionId: string; previousResultFingerprint: string
  approvedRequirementFingerprint: string; approvedPlanFingerprint: string; originalBaseCommitSha: string
  affectedApprovedOperations: ApprovedOperationReference[]
  rootCauseSummary: string; correctionStrategy: string; expectedBehavior: string; verificationImpact: string
  risks: string[]; proposalFingerprint: string; status: 'AwaitingApproval' | 'Approved'; createdAt: string; approvedAt: string | null
}
export interface CorrectionEligibility {
  canGenerateFailureAnalysis: boolean; canApproveCorrection: boolean; canGenerateCorrection: boolean
  canApproveCorrectedRevision: boolean; canGenerateReplacementVerificationPlan: boolean
}
export type FailureAnalysisAttemptStatus = 'Prepared' | 'DispatchMayHaveStarted' | 'ResponseReceived' | 'Completed' | 'FailedBeforeDispatch' | 'RetryableProviderResponse' | 'RejectedProviderOutput' | 'AmbiguousAfterDispatch' | 'ExpiredBeforeDispatch' | 'InterruptedAfterResponse'
export type CorrectionGenerationAttemptStatus = 'Prepared' | 'DispatchMayHaveStarted' | 'ResponseReceived' | 'OutputAccepted' | 'CheckoutVerified' | 'RevisionReserved' | 'WorkspacePreparing' | 'WorkspacePrepared' | 'MutationStarted' | 'ApplyCompleted' | 'ResultPersisted' | 'FailedBeforeDispatch' | 'FailedBeforeMutation' | 'AmbiguousAfterDispatch' | 'InterruptedAfterResponse' | 'RecoveryRequired' | 'Completed'
export interface FailureAnalysisGenerationAttempt {
  commandId: string; expectedFailedAttemptId: string; expectedFailedAttemptFingerprint: string; startedAt: string; leaseExpiresAt: string; updatedAt: string; completedAt: string | null; status: FailureAnalysisAttemptStatus
  logicalCallCount: number; physicalRequestCount: number; possiblyDispatchedRequestCount: number; definitelyUndispatchedRequestCount: number; activeRequestCount: number
  modelCallIds: string[]; providerResponses: VerificationProviderResponseTelemetry[]
  resultAnalysisId: string | null; failureCategory: string | null; failureMessage: string | null; retryEligible: boolean; recoveryRequired: boolean
}
export interface CorrectionGenerationAttempt {
  attemptId: string; commandId: string; proposalId: string; proposalFingerprint: string; previousRevisionId: string; previousResultFingerprint: string; startedAt: string; leaseExpiresAt: string; updatedAt: string; completedAt: string | null; status: CorrectionGenerationAttemptStatus
  logicalCallCount: number; physicalRequestCount: number; possiblyDispatchedRequestCount: number; definitelyUndispatchedRequestCount: number; activeRequestCount: number
  modelCallIds: string[]; providerResponses: VerificationProviderResponseTelemetry[]
  acceptedOutputFingerprint: string | null; revisionId: string | null
  failureCategory: string | null; failureMessage: string | null; retryEligible: boolean; recoveryRequired: boolean
}
export interface CorrectionApprovalAudit { commandId: string; proposalId: string; proposalFingerprint: string; expectedRowVersion: number; completedRowVersion: number; createdAt: string; completedAt: string; result: 'Approved' }
export interface DeliveryProposal {
  deliveryProposalId: string; proposalNumber: number; currentApprovedRevisionId: string
  currentImplementationResultFingerprint: string; currentVerificationPlanId: string
  currentVerificationPlanFingerprint: string; passedManualAttemptId: string; passedManualAttemptFingerprint: string
  baseCommitSha: string; remoteName: 'origin'; gitHubRepositoryOwner: string; gitHubRepositoryName: string
  targetBaseBranch: 'main'; targetBaseCommitShaAtPreparation: string; deliveryBranch: string
  commitMessage: string; pullRequestTitle: string; pullRequestBody: string; changedPaths: string[]
  proposalFingerprint: string; createdAt: string; status: 'Prepared' | 'Approved' | 'Delivered'; approvedAt: string | null
}
export type DeliveryAttemptPhase = 'Prepared' | 'WorktreeVerified' | 'StagingStarted' | 'CommitCreated' | 'PushStarted' | 'BranchPushed' | 'PullRequestCreationStarted' | 'PullRequestCreated' | 'FailedBeforeMutation' | 'RecoveryRequired'
export interface DeliveryAttempt {
  attemptId: string; attemptNumber: number; commandId: string; deliveryProposalId: string; deliveryProposalFingerprint: string
  startedAt: string; updatedAt: string; completedAt: string | null; leaseExpiresAt: string; phase: DeliveryAttemptPhase
  commitSha: string | null; remoteBranchSha: string | null; pullRequestNumber: number | null; pullRequestUrl: string | null
  safeFailureCategory: string | null; safeFailureMessage: string | null; recoveryRequired: boolean
  activeCheckoutVerifiedBefore: boolean; activeCheckoutVerifiedAfter: boolean; legacyCanonicalizationUsed: boolean
}
export interface DeliveryEligibility { canPrepareDelivery: boolean; canApproveDelivery: boolean; canExecuteDelivery: boolean; canReconcileDelivery: boolean; deliveryRecoveryRequired: boolean; pullRequestCreated: boolean }
export interface VerificationFailureDetails {
  title: string; expectedResult: string; actualResult: string; reproductionSteps: string[]; environmentNotes: string[]
  errorMessage: string | null; evidenceDescriptions: string[]; severity: 'Low' | 'Medium' | 'High' | 'Critical'
}
export interface ManualCaseResultRevision {
  resultRevisionId: string; revisionNumber: number; testCaseId: string; result: VerificationCaseResult; recordedAt: string
  notes: string | null; actualResult: string | null; evidenceDescriptions: string[]; notApplicableReason: string | null
  failureDetails: VerificationFailureDetails | null; supersedesResultRevisionId: string | null; trustLabel: string
}
export interface ManualVerificationAttempt {
  attemptId: string; attemptNumber: number; verificationPlanId: string; verificationPlanFingerprint: string
  implementationRevisionId: string; implementationResultFingerprint: string; startedAt: string; completedAt: string | null
  status: 'InProgress' | 'CompletedPassed' | 'CompletedFailed'; resultRevisions: ManualCaseResultRevision[]
  currentCaseResults: ManualCaseResultRevision[]; completionConfirmation: boolean | null; summary: string | null
  attemptFingerprint: string | null; passedAt: string | null; failedAt: string | null; trustLabel: string
}
export interface VerificationEligibility {
  canGenerateVerificationPlan: boolean; canStartVerificationAttempt: boolean; canRecordVerificationResult: boolean
  canCompleteVerificationPassed: boolean; canCompleteVerificationFailed: boolean; readyForDelivery: boolean
  ineligibilityReason: string | null
  isInitialVerificationPlanGeneration: boolean; canRetryVerificationPlanGeneration: boolean
  verificationGenerationStatus: 'NotStarted' | 'Active' | 'FailedBeforeDispatch' | 'RetryableProviderResponse' | 'RejectedProviderOutput' | 'AmbiguousAfterDispatch' | 'InterruptedBeforeDispatch' | 'Completed' | null
  verificationGenerationStatusMessage: string | null
}
export interface VerificationProviderResponseTelemetry {
  logicalCallId: string; startedAt: string; receivedAt: string; providerResponseId: string | null; providerRequestId: string | null
  status: 'Unknown' | 'Queued' | 'InProgress' | 'Completed' | 'Incomplete' | 'Failed' | 'Cancelled'
  incompleteReason: string | null; usageAvailability: 'Complete' | 'Partial' | 'Unavailable'; inputTokens: number | null
  cachedInputTokens: number | null; outputTokens: number | null; reasoningTokens: number | null
  httpStatusCode: number; dispatchDisposition: 'ResponseReceived'
}
export interface EngineeringTask {
  id: string
  repository: string
  originalRequirement: string
  currentClarifiedRequirement: string
  clarificationAnswers: ClarificationAnswer[]
  requirementRevisionNotes: RequirementRevision[]
  planRevisionNotes: PlanRevision[]
  currentPendingQuestion: string | null
  requirementSummary: string | null
  status: WorkflowStatus
  createdAt: string
  updatedAt: string
  requirementApprovedAt: string | null
  planApprovedAt: string | null
  repositorySnapshot: RepositorySnapshot | null
  evidenceItems: EvidenceItem[]
  evidenceFilesInspected: number
  evidenceFilesSelected: number
  totalEvidenceCharacters: number
  implementationPlan: ImplementationPlan | null
  repositoryAnalyzedAt: string | null
  repositoryFingerprint: string | null
  planCreatedAt: string | null
  implementationWorkspace: ImplementationWorkspace | null
  implementationResult: ImplementationResult | null
  lastImplementationFailure: ImplementationFailure | null
  implementationStartedAt: string | null
  implementationCompletedAt: string | null
  implementationRuntime: ImplementationRuntime | null
  rowVersion: number
  activeImplementationRevisionId: string | null
  approvedImplementationRevisionId: string | null
  pendingImplementationRevisionId?: string | null
  implementationRevisions: ImplementationRevision[]
  telemetry: ModelTelemetry
  currentVerificationPlanId?: string | null
  currentVerificationAttemptId?: string | null
  verificationPlans?: VerificationPlan[]
  verificationPlanGenerationAttempts?: Array<{ commandId: string; startedAt: string; leaseExpiresAt: string; completedAt: string | null; status: 'Prepared' | 'DispatchMayHaveStarted' | 'ResponseReceived' | 'Completed' | 'FailedBeforeDispatch' | 'RetryableProviderResponse' | 'RejectedProviderOutput' | 'AmbiguousAfterDispatch' | 'InterruptedBeforeDispatch'; failureCategory: string | null; failureMessage: string | null; resultPlanId: string | null; modelCallIds: string[]; lastLogicalCallId: string | null; logicalCallCount: number; physicalRequestCount: number; possiblyDispatchedRequestCount: number; logicalCalls: Array<{ logicalCallId: string; startedAt: string }>; providerResponses: VerificationProviderResponseTelemetry[] }>
  manualVerificationAttempts?: ManualVerificationAttempt[]
  verificationEligibility?: VerificationEligibility
  currentFailureAnalysisId?: string | null
  currentCorrectionProposalId?: string | null
  failureAnalyses?: FailureAnalysis[]
  correctionProposals?: CorrectionProposal[]
  failureAnalysisGenerationAttempts?: FailureAnalysisGenerationAttempt[]
  correctionGenerationAttempts?: CorrectionGenerationAttempt[]
  correctionApprovalAudit?: CorrectionApprovalAudit[]
  correctionEligibility?: CorrectionEligibility
  currentDeliveryProposalId?: string | null
  currentDeliveryAttemptId?: string | null
  deliveryProposals?: DeliveryProposal[]
  deliveryAttempts?: DeliveryAttempt[]
  deliveryEligibility?: DeliveryEligibility
}
export interface EngineeringTaskSummary {
  id: string
  status: WorkflowStatus
  createdAt: string
  updatedAt: string
  repository: string
  originalRequirementPreview: string
  verificationStatus?: string | null
  verificationProgressSummary?: string | null
  readyForDelivery?: boolean
}
export interface SystemCapabilities {
  aiMode: 'Fake' | 'OpenAI' | string
  clarificationProvider: string
  clarificationModel: string
  reasoningEffort: string
  clarificationConfigured: boolean
  planningProvider: string
  planningModel: string
  planningReasoningEffort: string
  planningConfigured: boolean
  implementationProvider: string
  implementationModel: string | null
  implementationReasoningEffort: string | null
  implementationConfigured: boolean
  aiConfigured: boolean
  repositoryInspectionAvailable: boolean
  planningAvailable: boolean
  targetModificationAvailable: boolean
  implementationApprovalAvailable: boolean
  implementationCorrectionAvailable: boolean
  validationAvailable: boolean
  reviewAvailable: boolean
  pullRequestCreationAvailable: boolean
  fakeImplementationAvailable: boolean
  openAiImplementationAvailable: boolean
  silentFallbackSupported: boolean
  commitAvailable: boolean
  pushAvailable: boolean
  deliveryPullRequestAvailable: boolean
  verificationPlanningProvider?: string
  verificationPlanningModel?: string
  verificationPlanningReasoningEffort?: string
  verificationPlanningConfigured?: boolean
  manualResultRecordingAvailable?: boolean
  automatedValidationAvailable?: boolean
  failureAnalysisAvailable?: boolean
  failureAnalysisConfigured?: boolean
  implementationCorrectionConfigured?: boolean
  deliveryPullRequestConfigured?: boolean
  autoMergeAvailable?: boolean
  deliveryGitAvailable?: boolean
  deliveryGitHubCliAvailable?: boolean
}
