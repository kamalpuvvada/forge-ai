export type WorkflowStatus = 'Draft' | 'Clarifying' | 'RequirementSummaryReady' | 'AwaitingRequirementApproval' | 'ReadyForPlanning' | 'Planning' | 'AwaitingPlanApproval' | 'Implementing' | 'Validating' | 'Reviewing' | 'Completed' | 'Failed'

export interface ClarificationAnswer { question: string; answer: string; answeredAt: string }

export interface EngineeringTask {
  id: string
  repository: string
  originalRequirement: string
  currentClarifiedRequirement: string
  clarificationAnswers: ClarificationAnswer[]
  currentPendingQuestion: string | null
  requirementSummary: string | null
  status: WorkflowStatus
  createdAt: string
  updatedAt: string
  requirementApprovedAt: string | null
  planApprovedAt: string | null
}
