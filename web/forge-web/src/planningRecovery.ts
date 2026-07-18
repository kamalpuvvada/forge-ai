export type PlanningRecoveryAction = 'retry' | 'refresh' | 'reanalyze'

export function getPlanningRecovery(failureCategory: string | null, snapshotStale: boolean) {
  if (snapshotStale) return {
    heading: 'The repository needs a fresh read-only analysis.',
    action: 'reanalyze' as PlanningRecoveryAction,
    note: 'The changed repository invalidated the saved snapshot; analysis must be refreshed first.',
  }
  if (failureCategory === 'missing_direct_evidence') return {
    heading: 'Refresh repository evidence before generating another plan.',
    action: 'refresh' as PlanningRecoveryAction,
    note: 'Refreshing reuses the saved snapshot and makes no model call. Plan generation remains a separate action.',
  }
  return {
    heading: 'The existing evidence is ready for another attempt.',
    action: 'retry' as PlanningRecoveryAction,
    note: 'Retry uses this snapshot and selected evidence. Repository analysis is not repeated.',
  }
}

export function getReadyPlanningAction(hasSelectedEvidence: boolean) {
  return hasSelectedEvidence
    ? { eyebrow: 'EVIDENCE REFRESHED', button: 'Generate plan', usesSavedEvidence: true }
    : { eyebrow: 'REQUIREMENT APPROVED', button: 'Analyze repository and create plan', usesSavedEvidence: false }
}
