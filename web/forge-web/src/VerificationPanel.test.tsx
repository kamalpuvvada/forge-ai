// @vitest-environment jsdom

import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { VerificationPanel } from './VerificationPanel'
import { decodeEngineeringTask } from './api'
import type { EngineeringTask, ManualVerificationAttempt, VerificationPlan } from './types'

const plan: VerificationPlan = {
  planId: '11111111-1111-4111-8111-111111111111', planNumber: 1,
  implementationRevisionId: '22222222-2222-4222-8222-222222222222', implementationResultFingerprint: 'a'.repeat(64),
  approvedRequirementFingerprint: 'b'.repeat(64), approvedPlanFingerprint: 'c'.repeat(64), generationContextFingerprint: 'd'.repeat(64),
  generatedAt: '2026-07-20T12:00:00Z', source: 'DeterministicFake', model: null, reasoningEffort: null,
  summary: 'Mechanical manual verification plan.', scope: 'Exact approved revision only.', preconditions: ['Use disposable tooling.'],
  testCases: [{ testCaseId: '33333333-3333-4333-8333-333333333333', order: 1, title: 'Manual check', objective: 'Observe behavior.', category: 'ManualBehavior', isRequired: true, preconditions: [], testData: [], orderedSteps: [{ order: 1, instruction: 'Inspect manually.', approvedValidationCommandId: null, expectedObservation: 'Expected behavior.' }], expectedResult: 'Behavior matches.', negativeOrEdgeCases: [], regressionScope: [], evidenceRequirements: [], safetyNotes: [] }],
  risks: [], limitations: [], evidenceGuidance: [], planFingerprint: 'e'.repeat(64), status: 'Current', trustLabel: 'FORGE GENERATED', executionLabel: 'MANUAL — NOT EXECUTED BY FORGE',
}

const revision = {
  revisionId: plan.implementationRevisionId, revisionNumber: 1, kind: 'Initial', previousRevisionId: null,
  planFingerprint: '9'.repeat(64), baseCommitSha: '8'.repeat(40), generationStartedAt: '2026-07-20T11:00:00Z',
  generationCompletedAt: '2026-07-20T11:10:00Z', generationState: 'Succeeded', reviewState: 'Approved',
  failureCategory: null, failureMessage: null, resultFingerprint: plan.implementationResultFingerprint,
  changedFileCount: 0, correctionSubmittedAt: null, approvedAt: '2026-07-20T11:20:00Z',
  isCurrent: true, isApproved: true,
}

function attempt(status: ManualVerificationAttempt['status'] = 'InProgress'): ManualVerificationAttempt {
  const completed = status !== 'InProgress'
  return {
    attemptId: '55555555-5555-4555-8555-555555555555', attemptNumber: 1,
    verificationPlanId: plan.planId, verificationPlanFingerprint: plan.planFingerprint,
    implementationRevisionId: plan.implementationRevisionId,
    implementationResultFingerprint: plan.implementationResultFingerprint,
    startedAt: '2026-07-20T12:01:00Z', completedAt: completed ? '2026-07-20T12:10:00Z' : null,
    status, resultRevisions: [], currentCaseResults: [], completionConfirmation: completed ? true : null,
    summary: null, attemptFingerprint: completed ? 'f'.repeat(64) : null,
    passedAt: status === 'CompletedPassed' ? '2026-07-20T12:10:00Z' : null,
    failedAt: status === 'CompletedFailed' ? '2026-07-20T12:10:00Z' : null, trustLabel: 'USER REPORTED',
  }
}

function manualEligibility(overrides: Record<string, unknown> = {}) {
  return {
    canGenerateVerificationPlan: false, canStartVerificationAttempt: false,
    canRecordVerificationResult: false, canCompleteVerificationPassed: false,
    canCompleteVerificationFailed: false, readyForDelivery: false, ineligibilityReason: null,
    isInitialVerificationPlanGeneration: false, canRetryVerificationPlanGeneration: false,
    verificationGenerationStatus: 'Completed', verificationGenerationStatusMessage: null, ...overrides,
  }
}

function task(status: EngineeringTask['status'], overrides: Record<string, unknown> = {}): EngineeringTask {
  const terminalAttempt = status === 'ReadyForDelivery' ? attempt('CompletedPassed') :
    status === 'ManualVerificationFailed' ? attempt('CompletedFailed') : null
  const hasPlan = ['AwaitingManualVerification', 'ReadyForDelivery', 'ManualVerificationFailed'].includes(status)
  const eligibility = {
    canGenerateVerificationPlan: status === 'ImplementationApproved',
    canStartVerificationAttempt: status === 'AwaitingManualVerification' && terminalAttempt === null,
    canRecordVerificationResult: false, canCompleteVerificationPassed: false,
    canCompleteVerificationFailed: false, readyForDelivery: status === 'ReadyForDelivery',
    ineligibilityReason: null, isInitialVerificationPlanGeneration: status === 'ImplementationApproved',
    canRetryVerificationPlanGeneration: false,
    verificationGenerationStatus: status === 'ImplementationApproved' ? 'NotStarted' : hasPlan ? 'Completed' : null,
    verificationGenerationStatusMessage: null,
  }
  const json = {
    id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', status, rowVersion: 4,
    repository: 'safe-repository', originalRequirement: 'Verify safely.',
    currentClarifiedRequirement: 'Verify safely.', clarificationAnswers: [], requirementRevisionNotes: [],
    planRevisionNotes: [], currentPendingQuestion: null, requirementSummary: 'Verify safely.',
    createdAt: '2026-07-20T10:00:00Z', updatedAt: '2026-07-20T12:10:00Z',
    requirementApprovedAt: '2026-07-20T10:10:00Z', planApprovedAt: '2026-07-20T10:20:00Z',
    repositorySnapshot: null, evidenceItems: [], evidenceFilesInspected: 0, evidenceFilesSelected: 0,
    totalEvidenceCharacters: 0, implementationPlan: null, repositoryAnalyzedAt: null,
    repositoryFingerprint: null, planCreatedAt: null, implementationWorkspace: null,
    implementationResult: null, lastImplementationFailure: null, implementationStartedAt: null,
    implementationCompletedAt: null, implementationRuntime: null,
    activeImplementationRevisionId: plan.implementationRevisionId,
    approvedImplementationRevisionId: plan.implementationRevisionId, implementationRevisions: [revision],
    telemetry: { totalCalls: 0, usageAvailability: 'Complete', usageUnavailableCallCount: 0,
      totalInputTokens: 0, totalCachedInputTokens: 0, totalOutputTokens: 0, totalReasoningTokens: 0,
      totalEstimatedCostUsd: 0, costUnavailableCallCount: 0, isPartialEstimate: false,
      verificationLogicalAttemptCount: 0, verificationPhysicalRequestCount: 0,
      verificationPossiblyDispatchedRequestCount: 0, verificationDefinitelyUndispatchedAttemptCount: 0,
      completeEstimatedSubtotalUsd: null, partialEstimatedSubtotalUsd: null, availableEstimatedSubtotalUsd: 0,
      hasPartialEstimates: false, possiblyDispatchedUnavailableEstimatedCostCallCount: 0, calls: [] },
    currentVerificationPlanId: hasPlan ? plan.planId : null,
    currentVerificationAttemptId: terminalAttempt?.attemptId ?? null,
    verificationPlans: hasPlan ? [{ ...plan, status: terminalAttempt ? 'Completed' : 'Current' }] : [],
    verificationPlanGenerationAttempts: [], manualVerificationAttempts: terminalAttempt ? [terminalAttempt] : [],
    verificationEligibility: eligibility, ...overrides,
  }
  return decodeEngineeringTask(json)
}

let container: HTMLDivElement | null = null
afterEach(() => { container?.remove(); container = null })

async function render(status: EngineeringTask['status'], overrides: Record<string, unknown> = {}) {
  container = document.createElement('div'); document.body.append(container)
  const props = { task: task(status, overrides), capabilities: null, busy: false,
    onGenerate: vi.fn(), onStart: vi.fn(), onSaveCase: vi.fn(), onComplete: vi.fn(), onExportPlan: vi.fn() }
  const root = createRoot(container)
  await act(async () => root.render(<VerificationPanel {...props} />))
  return { props, root }
}

describe('VerificationPanel', () => {
  it('uses backend eligibility to distinguish active, ambiguous, and explicitly retryable generation', async () => {
    const activeEligibility = {
      canGenerateVerificationPlan: false, canStartVerificationAttempt: false, canRecordVerificationResult: false,
      canCompleteVerificationPassed: false, canCompleteVerificationFailed: false, readyForDelivery: false,
      ineligibilityReason: 'Generation is active.', isInitialVerificationPlanGeneration: false,
      canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'Active' as const,
      verificationGenerationStatusMessage: 'Verification-plan generation is active. Forge will not automatically redispatch.',
    }
    const active = await render('VerificationPlanning', { verificationEligibility: activeEligibility })
    expect(container?.textContent).toContain('active. Forge will not automatically redispatch')
    let button = container!.querySelector<HTMLButtonElement>('button.primary-button')!
    expect(button.disabled).toBe(true)
    await act(async () => active.root.unmount())

    const ambiguous = await render('VerificationPlanning', { verificationEligibility: {
      ...activeEligibility, verificationGenerationStatus: 'AmbiguousAfterDispatch',
      verificationGenerationStatusMessage: 'Provider dispatch may have occurred. Retry is disabled and Forge cannot determine billing status.',
    } })
    expect(container?.textContent).toContain('cannot determine billing status')
    button = container!.querySelector<HTMLButtonElement>('button.primary-button')!
    expect(button.disabled).toBe(true)
    await act(async () => ambiguous.root.unmount())

    const retry = await render('VerificationPlanning', { verificationEligibility: {
      ...activeEligibility, canGenerateVerificationPlan: true, canRetryVerificationPlanGeneration: true,
      verificationGenerationStatus: 'FailedBeforeDispatch',
      verificationGenerationStatusMessage: 'The request failed definitely before dispatch and is eligible for explicit retry.',
    } })
    button = [...container!.querySelectorAll('button')].find(item => item.textContent?.includes('Retry'))!
    expect(button.disabled).toBe(false)
    await act(async () => button.click())
    expect(retry.props.onGenerate).toHaveBeenCalledOnce()
    await act(async () => retry.root.unmount())
  })

  it('labels initial generation separately from a safe retry', async () => {
    const initial = await render('ImplementationApproved', { currentVerificationPlanId: null,
      verificationPlans: [], verificationEligibility: {
        canGenerateVerificationPlan: true, canStartVerificationAttempt: false, canRecordVerificationResult: false,
        canCompleteVerificationPassed: false, canCompleteVerificationFailed: false, readyForDelivery: false,
        ineligibilityReason: null, isInitialVerificationPlanGeneration: true,
        canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'NotStarted',
        verificationGenerationStatusMessage: 'Manual verification-plan generation has not started.',
      } })
    const button = [...container!.querySelectorAll('button')].find(item => item.textContent === 'Generate manual verification plan')!
    expect(button.disabled).toBe(false)
    await act(async () => button.click())
    expect(initial.props.onGenerate).toHaveBeenCalledOnce()
    await act(async () => initial.root.unmount())
  })

  it.each([
    ['Active', 'The provider response is recorded and Forge is finalizing the verification plan.'],
    ['AmbiguousAfterDispatch', 'A provider response was recorded. Retry is disabled to avoid a duplicate billable request.'],
  ] as const)('renders response-phase %s state without offering retry', async (status, message) => {
    const view = await render('VerificationPlanning', { verificationEligibility: {
      canGenerateVerificationPlan: false, canStartVerificationAttempt: false, canRecordVerificationResult: false,
      canCompleteVerificationPassed: false, canCompleteVerificationFailed: false, readyForDelivery: false,
      ineligibilityReason: message, isInitialVerificationPlanGeneration: false,
      canRetryVerificationPlanGeneration: false, verificationGenerationStatus: status,
      verificationGenerationStatusMessage: message,
    } })
    expect(container?.textContent).toContain(message)
    expect(container?.textContent).not.toContain('Retry verification-plan generation')
    expect(container!.querySelector<HTMLButtonElement>('button.primary-button')?.disabled).toBe(true)
    await act(async () => view.root.unmount())
  })

  it('shows the bound plan and starts a manual attempt', async () => {
    const { props, root } = await render('AwaitingManualVerification')
    expect(container?.textContent).toContain('Mechanical manual verification plan.')
    expect(container?.textContent).toContain('FORGE GENERATED')
    const button = [...container!.querySelectorAll('button')].find(item => item.textContent === 'Start verification')!
    await act(async () => button.click())
    expect(props.onStart).toHaveBeenCalledOnce()
    await act(async () => root.unmount())
  })

  it('keeps Start disabled and dispatches nothing when backend eligibility is false', async () => {
    const view = await render('AwaitingManualVerification', {
      verificationEligibility: manualEligibility({ canStartVerificationAttempt: false }),
    })
    const button = [...container!.querySelectorAll('button')].find(item => item.textContent === 'Start verification')!
    expect(button.disabled).toBe(true)
    await act(async () => button.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    expect(view.props.onStart).not.toHaveBeenCalled()
    await act(async () => view.root.unmount())
  })

  it('keeps Save and completion handlers inert when an active attempt is ineligible', async () => {
    const activeAttempt = attempt()
    const view = await render('AwaitingManualVerification', {
      currentVerificationAttemptId: activeAttempt.attemptId,
      manualVerificationAttempts: [activeAttempt],
      verificationEligibility: manualEligibility(),
    })
    const save = [...container!.querySelectorAll('button')].find(item => item.textContent === 'Save case result')!
    expect(save.disabled).toBe(true)
    const form = container!.querySelector<HTMLFormElement>('form.verification-case')!
    await act(async () => form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
    expect(view.props.onSaveCase).not.toHaveBeenCalled()
    for (const label of ['Complete as passed', 'Complete as failed']) {
      const button = [...container!.querySelectorAll('button')].find(item => item.textContent === label)!
      expect(button.disabled).toBe(true)
      await act(async () => button.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    }
    expect(view.props.onComplete).not.toHaveBeenCalled()
    await act(async () => view.root.unmount())
  })

  it('uses a resolved active attempt and backend Record eligibility without enabling Start', async () => {
    const activeAttempt = attempt()
    const view = await render('AwaitingManualVerification', {
      currentVerificationAttemptId: activeAttempt.attemptId,
      manualVerificationAttempts: [activeAttempt],
      verificationEligibility: manualEligibility({ canRecordVerificationResult: true }),
    })
    expect(Array.from(container!.querySelectorAll('button')).some(button =>
      button.textContent === 'Start verification')).toBe(false)
    const select = container!.querySelector<HTMLSelectElement>('select')!
    await act(async () => {
      select.value = 'Passed'
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })
    const save = [...container!.querySelectorAll('button')].find(item => item.textContent === 'Save case result')!
    expect(save.disabled).toBe(false)
    await act(async () => container!.querySelector<HTMLFormElement>('form.verification-case')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
    expect(view.props.onSaveCase).toHaveBeenCalledOnce()
    await act(async () => view.root.unmount())
  })

  it('rejects missing eligibility through the production decoder before rendering actions', () => {
    expect(() => task('VerificationPlanning', { verificationEligibility: undefined }))
      .toThrowError('The task response could not be validated safely.')
  })

  it('states the exact ReadyForDelivery boundary', async () => {
    const { root } = await render('ReadyForDelivery')
    expect(container?.textContent).toContain('Manual verification passed — user reported')
    expect(container?.textContent).toContain('No automated validation, commit, push, or pull request')
    await act(async () => root.unmount())
  })

  it('preserves failed status without offering analysis or correction', async () => {
    const { root } = await render('ManualVerificationFailed')
    expect(container?.textContent).toContain('MANUAL VERIFICATION FAILED · USER REPORTED')
    expect(container?.textContent).toContain('Failure analysis and implementation correction are unavailable')
    await act(async () => root.unmount())
  })
})
