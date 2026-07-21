import type { VerificationCaseResult, VerificationFailureDetails } from './types'

export interface CaseFormValue {
  result: VerificationCaseResult
  notes: string
  actualResult: string
  evidenceDescriptions: string[]
  notApplicableReason: string
  failureDetails: VerificationFailureDetails | null
}

export const blankCaseFormValue: CaseFormValue = {
  result: 'NotStarted',
  notes: '',
  actualResult: '',
  evidenceDescriptions: [],
  notApplicableReason: '',
  failureDetails: null,
}

export function isCaseFormValueValid(value: CaseFormValue) {
  if (value.result === 'NotStarted') return false
  if (value.result === 'NotApplicable')
    return value.notApplicableReason.trim().length > 0 && value.failureDetails === null
  if (value.notApplicableReason.trim().length > 0) return false
  if (value.result !== 'Failed' && value.result !== 'Blocked') return value.failureDetails === null
  const failure = value.failureDetails
  return failure !== null && failure.title.trim().length > 0 && failure.actualResult.trim().length > 0 &&
    failure.reproductionSteps.some(step => step.trim().length > 0)
}
