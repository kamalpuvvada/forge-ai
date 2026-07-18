export type WorkflowStatus = 'Draft' | 'Clarifying' | 'RequirementSummaryReady' | 'AwaitingRequirementApproval' | 'ReadyForPlanning' | 'Planning' | 'AwaitingPlanApproval' | 'Implementing' | 'Validating' | 'Reviewing' | 'Completed' | 'Failed'
export interface ClarificationAnswer { question: string; answer: string; answeredAt: string }
export interface RequirementRevision { correction: string; previousSummary: string; submittedAt: string }
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
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  reasoningTokens: number | null
  estimatedCostUsd: number
  failureCategory: string | null
}
export interface ModelTelemetry {
  totalCalls: number
  totalInputTokens: number
  totalCachedInputTokens: number
  totalOutputTokens: number
  totalEstimatedCostUsd: number
  calls: ModelCall[]
}
export interface EngineeringTask {
  id: string
  repository: string
  originalRequirement: string
  currentClarifiedRequirement: string
  clarificationAnswers: ClarificationAnswer[]
  requirementRevisionNotes: RequirementRevision[]
  currentPendingQuestion: string | null
  requirementSummary: string | null
  status: WorkflowStatus
  createdAt: string
  updatedAt: string
  requirementApprovedAt: string | null
  planApprovedAt: string | null
  telemetry: ModelTelemetry
}
export interface SystemCapabilities {
  aiMode: 'Fake' | 'OpenAI' | string
  clarificationModel: string
  reasoningEffort: string
  aiConfigured: boolean
  repositoryInspectionAvailable: boolean
  planningAvailable: boolean
}
