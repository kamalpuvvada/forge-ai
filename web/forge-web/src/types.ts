export type WorkflowStatus = 'Draft' | 'Clarifying' | 'RequirementSummaryReady' | 'AwaitingRequirementApproval' | 'ReadyForPlanning' | 'Planning' | 'AwaitingPlanApproval' | 'PlanApproved' | 'Implementing' | 'Validating' | 'Reviewing' | 'Completed' | 'Failed'
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
}
export interface ModelTelemetry {
  totalCalls: number
  totalInputTokens: number
  totalCachedInputTokens: number
  totalOutputTokens: number
  totalEstimatedCostUsd: number
  costUnavailableCallCount: number
  isPartialEstimate: boolean
  calls: ModelCall[]
}
export interface RepositoryFile { relativePath: string; extension: string; sizeBytes: number; lineCount: number; probableRole: string; isTest: boolean; association: string | null; declaredSymbols: string[] }
export interface RepositorySnapshot {
  normalizedRoot: string; isGitRepository: boolean; branch: string | null; shortHeadSha: string | null; fullHeadSha: string | null
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
  telemetry: ModelTelemetry
}
export interface EngineeringTaskSummary {
  id: string
  status: WorkflowStatus
  createdAt: string
  updatedAt: string
  repository: string
  originalRequirementPreview: string
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
  aiConfigured: boolean
  repositoryInspectionAvailable: boolean
  planningAvailable: boolean
  targetModificationAvailable: boolean
  validationAvailable: boolean
  reviewAvailable: boolean
  pullRequestCreationAvailable: boolean
}
