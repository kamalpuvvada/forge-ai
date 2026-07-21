// @vitest-environment jsdom

import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { VerificationPanel } from './VerificationPanel'
import { decodeEngineeringTask } from './api'
import type { EngineeringTask, ManualCaseResultRevision, ManualVerificationAttempt, SystemCapabilities, VerificationPlan, VerificationTestCase } from './types'

const plan: VerificationPlan = {
  planId: '11111111-1111-4111-8111-111111111111', planNumber: 1,
  implementationRevisionId: '22222222-2222-4222-8222-222222222222', implementationResultFingerprint: 'a'.repeat(64),
  approvedRequirementFingerprint: 'b'.repeat(64), approvedPlanFingerprint: 'c'.repeat(64), generationContextFingerprint: 'd'.repeat(64),
  generatedAt: '2026-07-20T12:00:00Z', source: 'DeterministicFake', model: null, reasoningEffort: null,
  summary: 'Mechanical manual verification plan.', scope: 'Exact approved revision only.', preconditions: ['Use disposable tooling.'],
  testCases: [{ testCaseId: '33333333-3333-4333-8333-333333333333', order: 1, title: 'Manual check', objective: 'Observe behavior.', category: 'ManualBehavior', isRequired: true, preconditions: [], testData: [], orderedSteps: [{ order: 1, instruction: 'Inspect manually.', approvedValidationCommandId: null, expectedObservation: 'Expected behavior.' }], expectedResult: 'Behavior matches.', negativeOrEdgeCases: [], regressionScope: [], evidenceRequirements: [], safetyNotes: [] }],
  risks: [], limitations: [], evidenceGuidance: [], planFingerprint: 'e'.repeat(64), status: 'Current', trustLabel: 'FORGE GENERATED', executionLabel: 'MANUAL — NOT EXECUTED BY FORGE',
}

const secondCase: VerificationTestCase = {
  ...plan.testCases[0], testCaseId: '44444444-4444-4444-8444-444444444444', order: 2,
  title: 'Second manual check', objective: 'Observe the second behavior.', expectedResult: 'Second behavior matches.',
}

const multiCasePlan: VerificationPlan = { ...plan, testCases: [plan.testCases[0], secondCase] }

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

function caseResult(testCaseId: string, result: ManualCaseResultRevision['result'] = 'Passed',
  overrides: Partial<ManualCaseResultRevision> = {}): ManualCaseResultRevision {
  return {
    resultRevisionId: testCaseId === plan.testCases[0].testCaseId
      ? '66666666-6666-4666-8666-666666666666'
      : '77777777-7777-4777-8777-777777777777',
    revisionNumber: 1, testCaseId, result, recordedAt: '2026-07-20T12:05:00Z', notes: 'Recorded manually.',
    actualResult: 'Observed expected behavior.', evidenceDescriptions: [], notApplicableReason: null,
    failureDetails: null, supersedesResultRevisionId: null, trustLabel: 'USER REPORTED', ...overrides,
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
    verificationEligibility: eligibility,
    correctionEligibility: {
      canGenerateFailureAnalysis: status === 'ManualVerificationFailed',
      canApproveCorrection: status === 'AwaitingCorrectionApproval',
      canGenerateCorrection: status === 'CorrectionApproved', canApproveCorrectedRevision: false,
      canGenerateReplacementVerificationPlan: false,
    },
    ...overrides,
  }
  return decodeEngineeringTask(json)
}

function activeOverrides(results: ManualCaseResultRevision[] = [], selectedPlan = multiCasePlan) {
  const activeAttempt = { ...attempt(), resultRevisions: results, currentCaseResults: results }
  return {
    rowVersion: 5 + results.length,
    currentVerificationAttemptId: activeAttempt.attemptId,
    verificationPlans: [selectedPlan],
    manualVerificationAttempts: [activeAttempt],
    verificationEligibility: manualEligibility({ canRecordVerificationResult: true }),
  }
}

function failedOverrides() {
  const failureDetails = {
    title: 'Observed mismatch', expectedResult: 'Behavior matches.', actualResult: 'Behavior differed.',
    reproductionSteps: ['Repeat the manual check.'], environmentNotes: ['Disposable environment.'],
    errorMessage: 'Synthetic mismatch.', evidenceDescriptions: [], severity: 'High' as const,
  }
  const failed = caseResult(plan.testCases[0].testCaseId, 'Failed', { failureDetails, actualResult: 'Behavior differed.' })
  const failedAttempt = { ...attempt('CompletedFailed'), resultRevisions: [failed], currentCaseResults: [failed] }
  return {
    currentVerificationAttemptId: failedAttempt.attemptId,
    manualVerificationAttempts: [failedAttempt],
    verificationPlans: [{ ...plan, status: 'Completed' }],
  }
}

let container: HTMLDivElement | null = null
afterEach(() => { container?.remove(); container = null })

async function renderSelected(selected: EngineeringTask, activeAction: string | null = null,
  capabilities: SystemCapabilities | null = null) {
  container = document.createElement('div'); document.body.append(container)
  const props = { task: selected, capabilities, busy: activeAction !== null, activeAction, documentsBusy: false,
    documentError: null, onGenerate: vi.fn(), onStart: vi.fn(), onSaveCase: vi.fn(), onComplete: vi.fn(),
    onExportPlan: vi.fn(), onExportApprovedPlan: vi.fn(), onExportTaskReport: vi.fn(), onViewImplementation: vi.fn() }
  const root = createRoot(container)
  await act(async () => root.render(<VerificationPanel {...props} />))
  return { props, root }
}

async function render(status: EngineeringTask['status'], overrides: Record<string, unknown> = {},
  activeAction: string | null = null) {
  return renderSelected(task(status, overrides), activeAction)
}

describe('VerificationPanel', () => {
  it('allows a current plan to start when only superseded-plan attempts are preserved', async () => {
    const selected = task('AwaitingManualVerification')
    const historical = { ...attempt('CompletedFailed'), verificationPlanId: '99999999-9999-4999-8999-999999999999',
      verificationPlanFingerprint: '9'.repeat(64) }
    selected.manualVerificationAttempts = [historical]
    const view = await renderSelected(selected)
    const button = Array.from(container!.querySelectorAll('button')).find(item => item.textContent === 'Start verification')!
    expect(button.disabled).toBe(false)
    await act(async () => button.click())
    expect(view.props.onStart).toHaveBeenCalledOnce()
    await act(async () => view.root.unmount())
  })

  it('blocks Start only when an attempt belongs to the current plan', async () => {
    const currentAttempt = { ...attempt(), attemptNumber: 2, attemptId: '99999999-9999-4999-8999-999999999999' }
    const selected = task('AwaitingManualVerification')
    selected.currentVerificationAttemptId = currentAttempt.attemptId
    selected.manualVerificationAttempts = [
      { ...attempt('CompletedFailed'), verificationPlanId: '88888888-8888-4888-8888-888888888888' }, currentAttempt,
    ]
    selected.verificationEligibility = manualEligibility({ canRecordVerificationResult: true }) as
      EngineeringTask['verificationEligibility']
    const view = await renderSelected(selected)
    expect(Array.from(container!.querySelectorAll('button')).some(item => item.textContent === 'Start verification')).toBe(false)
    await act(async () => view.root.unmount())
  })

  it('ignores multiple attempts from other plans when gating the current plan', async () => {
    const selected = task('AwaitingManualVerification')
    selected.manualVerificationAttempts = [
      { ...attempt('CompletedFailed'), verificationPlanId: '77777777-7777-4777-8777-777777777777' },
      { ...attempt('CompletedPassed'), attemptId: '88888888-8888-4888-8888-888888888888', attemptNumber: 2,
        verificationPlanId: '99999999-9999-4999-8999-999999999999' },
    ]
    const view = await renderSelected(selected)
    expect(Array.from(container!.querySelectorAll<HTMLButtonElement>('button'))
      .find(item => item.textContent === 'Start verification')?.disabled).toBe(false)
    await act(async () => view.root.unmount())
  })

  it.each([
    ['ImplementationApproved', 'replacement-verification-plan', 'Generating verification plan 2…'],
    ['AwaitingManualVerification', 'start-verification', 'Starting verification…'],
  ] as const)('shows accessible pending feedback for %s', async (status, activeAction, label) => {
    const view = await render(status, {}, activeAction)
    const button = Array.from(container!.querySelectorAll('button')).find(item => item.textContent === label)
    expect(button?.disabled).toBe(true)
    expect(button?.getAttribute('aria-busy')).toBe('true')
    await act(async () => view.root.unmount())
  })

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

  it('offers an explicit Fake-mode replacement after locally rejected provider output', async () => {
    const selected = task('VerificationPlanning', { verificationEligibility: {
      canGenerateVerificationPlan: true, canStartVerificationAttempt: false, canRecordVerificationResult: false,
      canCompleteVerificationPassed: false, canCompleteVerificationFailed: false, readyForDelivery: false,
      ineligibilityReason: 'The completed provider output was rejected locally.',
      isInitialVerificationPlanGeneration: false, canRetryVerificationPlanGeneration: true,
      verificationGenerationStatus: 'RejectedProviderOutput',
      verificationGenerationStatusMessage: 'OpenAI completed the request, but Forge rejected the generated plan. You may explicitly generate a new plan; another provider request may incur a charge.',
    } })
    const capabilities = {
      verificationPlanningProvider: 'Fake', planningProvider: 'Fake',
    } as SystemCapabilities
    const view = await renderSelected(selected, null, capabilities)

    expect(container?.textContent).toContain('OpenAI completed the request, but Forge rejected the generated plan.')
    expect(container?.textContent).not.toContain('ambiguous')
    const button = [...container!.querySelectorAll('button')]
      .find(item => item.textContent === 'Generate replacement plan with Fake mode')!
    expect(button.disabled).toBe(false)
    await act(async () => button.click())
    expect(view.props.onGenerate).toHaveBeenCalledOnce()
    await act(async () => view.root.unmount())
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

  it('uses the compact Forge generation-card structure and preserves provider trust metadata', async () => {
    const view = await render('ImplementationApproved', { currentVerificationPlanId: null,
      verificationPlans: [], verificationEligibility: {
        canGenerateVerificationPlan: true, canStartVerificationAttempt: false, canRecordVerificationResult: false,
        canCompleteVerificationPassed: false, canCompleteVerificationFailed: false, readyForDelivery: false,
        ineligibilityReason: null, isInitialVerificationPlanGeneration: true,
        canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'NotStarted',
        verificationGenerationStatusMessage: 'Manual verification-plan generation has not started.',
      } })

    expect(container!.querySelector('.verification-generation')).not.toBeNull()
    expect(container!.querySelector('h2')?.textContent).toBe('Generate manual verification plan')
    expect(container!.querySelector('.verification-status-strip')?.textContent)
      .toContain('MANUAL — NOT EXECUTED BY FORGE')
    expect(container!.querySelector('.verification-generation-action [role="status"]')?.textContent)
      .toContain('has not started')
    await act(async () => view.root.unmount())
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

  it('keeps a long generated summary as body copy under a fixed concise heading', async () => {
    const longSummary = 'Generated summary '.repeat(40).trim()
    const view = await render('AwaitingManualVerification', {
      verificationPlans: [{ ...plan, summary: longSummary }],
    })

    expect(container!.querySelector('h2')?.textContent).toBe('Manual verification plan')
    expect(container!.querySelector('h2')?.textContent).not.toContain('Generated summary')
    expect(container!.querySelector('.verification-summary')?.textContent).toBe(longSummary)
    await act(async () => view.root.unmount())
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

  it('uses a compact responsive failed-case editor without removing any failure field', async () => {
    const view = await render('AwaitingManualVerification', activeOverrides())
    const result = container!.querySelector<HTMLSelectElement>('.verification-form-grid select')!
    await act(async () => {
      result.value = 'Failed'
      result.dispatchEvent(new Event('change', { bubbles: true }))
    })

    const card = container!.querySelector('.compact-failure-card')!
    expect(card).toBeTruthy()
    expect(card.querySelector('legend')?.textContent).toContain('Failure details required')
    expect(card.querySelector('legend')?.textContent).toContain('FAILED OR BLOCKED')
    expect(card.querySelector('.failure-title')?.textContent).toContain('Title')
    for (const label of ['Expected result', 'Actual result', 'Reproduction steps', 'Environment notes', 'Error message', 'Severity'])
      expect(card.textContent).toContain(label)
    expect(card.querySelector('.verification-failure-grid.responsive-form-grid')).toBeTruthy()
    const footer = card.querySelector('.verification-failure-footer')!
    expect(footer.textContent).toContain('Severity')
    expect(footer.textContent).toContain('Results are appended to immutable history.')
    const save = footer.querySelector<HTMLButtonElement>('button')!
    expect(save.textContent).toBe('Save case result')
    expect(save.disabled).toBe(true)
    expect(save.hasAttribute('aria-busy')).toBe(true)
    await act(async () => view.root.unmount())
  })

  it('keeps completion confirmation and pass or fail actions compact and explicit', async () => {
    const view = await render('AwaitingManualVerification', activeOverrides())
    const completion = container!.querySelector('.verification-completion')!
    const confirmation = completion.querySelector<HTMLLabelElement>('.verification-confirmation')!
    expect(confirmation.querySelector('input[type="checkbox"]')).toBeTruthy()
    expect(confirmation.textContent).toContain('recorded by a human')
    expect(completion.querySelector('textarea')?.getAttribute('rows')).toBe('2')
    const actions = completion.querySelector('.verification-completion-actions')!
    expect(actions.textContent).toContain('Complete as passed')
    expect(actions.textContent).toContain('Complete as failed')
    await act(async () => view.root.unmount())
  })

  it('offers the bound implementation diff globally and inside changed-file review cases', async () => {
    const changedFilePlan = { ...multiCasePlan, testCases: [
      { ...multiCasePlan.testCases[0], title: 'Changed file review', objective: 'Review the bounded diff.' },
      multiCasePlan.testCases[1],
    ] }
    const view = await render('AwaitingManualVerification', activeOverrides([], changedFilePlan))
    const actions = [...container!.querySelectorAll<HTMLButtonElement>('button')]
      .filter(button => button.textContent === 'View approved implementation diff')

    expect(actions).toHaveLength(2)
    expect(container!.textContent).toContain('Review the exact approved diff used by this verification plan.')
    expect(container!.querySelectorAll('.verification-document-actions button')).toHaveLength(3)
    await act(async () => actions[1].click())
    expect(view.props.onViewImplementation).toHaveBeenCalledOnce()
    expect(view.props.onViewImplementation).toHaveBeenCalledWith(changedFilePlan)
    await act(async () => view.root.unmount())
  })

  it('keeps a completed failed case collapsed to one textual summary control', async () => {
    const view = await render('ManualVerificationFailed', failedOverrides())
    const summary = container!.querySelector<HTMLButtonElement>('.verification-case-summary')!

    await act(async () => summary.click())

    expect(summary.textContent).toContain('01')
    expect(summary.textContent).toContain('Manual check')
    expect(summary.textContent).toContain('Required')
    expect(summary.textContent).toContain('Failed')
    expect(summary.getAttribute('aria-expanded')).toBe('false')
    expect(container!.querySelectorAll('.verification-case-summary')).toHaveLength(1)
    expect(summary.closest('.verification-case-card')!.querySelector('.verification-result-summary')).toBeNull()
    await act(async () => view.root.unmount())
  })

  it('renders keyboard-operable case disclosures with only one case expanded', async () => {
    const view = await render('AwaitingManualVerification', activeOverrides())
    const summaries = [...container!.querySelectorAll<HTMLButtonElement>('.verification-case-summary')]
    expect(summaries).toHaveLength(2)
    expect(summaries[0].tagName).toBe('BUTTON')
    expect(summaries[0].getAttribute('aria-expanded')).toBe('true')
    expect(summaries[1].getAttribute('aria-expanded')).toBe('false')

    summaries[1].focus()
    await act(async () => {
      summaries[1].dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
      summaries[1].click()
    })
    expect(document.activeElement).toBe(summaries[1])
    expect(summaries[0].getAttribute('aria-expanded')).toBe('false')
    expect(summaries[1].getAttribute('aria-expanded')).toBe('true')
    expect(container!.querySelectorAll('.verification-case-body')).toHaveLength(1)
    await act(async () => view.root.unmount())
  })

  it('opens the first unresolved required case instead of an already completed case', async () => {
    const firstPassed = caseResult(plan.testCases[0].testCaseId)
    const view = await render('AwaitingManualVerification', activeOverrides([firstPassed]))
    const summaries = [...container!.querySelectorAll<HTMLButtonElement>('.verification-case-summary')]

    expect(summaries[0].getAttribute('aria-expanded')).toBe('false')
    expect(summaries[0].textContent).toContain('Passed')
    expect(summaries[1].getAttribute('aria-expanded')).toBe('true')
    expect(summaries[1].textContent).toContain('NotStarted')
    await act(async () => view.root.unmount())
  })

  it('collapses a persisted Passed case and opens the next unresolved required case', async () => {
    const view = await render('AwaitingManualVerification', activeOverrides())
    const select = container!.querySelector<HTMLSelectElement>('select')!
    await act(async () => {
      select.value = 'Passed'
      select.dispatchEvent(new Event('change', { bubbles: true }))
    })
    await act(async () => container!.querySelector<HTMLFormElement>('form.verification-case')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })))
    expect(view.props.onSaveCase).toHaveBeenCalledOnce()

    const firstPassed = caseResult(plan.testCases[0].testCaseId)
    const savedTask = task('AwaitingManualVerification', activeOverrides([firstPassed]))
    await act(async () => view.root.render(<VerificationPanel {...view.props} task={savedTask} busy={false} />))
    const summaries = [...container!.querySelectorAll<HTMLButtonElement>('.verification-case-summary')]
    expect(summaries[0].getAttribute('aria-expanded')).toBe('false')
    expect(summaries[1].getAttribute('aria-expanded')).toBe('true')
    await act(async () => view.root.unmount())
  })

  it('shows completed result detail and append-only history inside its reopenable case card', async () => {
    const firstPassed = caseResult(plan.testCases[0].testCaseId)
    const view = await render('AwaitingManualVerification', activeOverrides([firstPassed]))
    const firstSummary = container!.querySelector<HTMLButtonElement>('.verification-case-summary')!
    await act(async () => firstSummary.click())

    const firstCard = firstSummary.closest('.verification-case-card')!
    expect(firstSummary.getAttribute('aria-expanded')).toBe('true')
    expect(firstCard.querySelector('.verification-result-summary')?.textContent).toContain('Passed · USER REPORTED')
    expect(firstCard.querySelector('.verification-case-history')?.textContent).toContain('Revision 1 · Passed')
    expect(firstCard.querySelector('.verification-case-history')?.textContent).toContain('USER REPORTED')
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
    expect(container!.querySelectorAll('.verification-metadata div')).toHaveLength(4)
    expect(container!.querySelector('.verification-metadata')?.textContent).toContain('Attempt fingerprint')
    expect(container!.querySelectorAll('.verification-document-actions button')).toHaveLength(3)
    expect(container!.querySelector('p button')).toBeNull()
    await act(async () => root.unmount())
  })

  it('preserves failed detail while the governed correction workflow remains available', async () => {
    const { root } = await render('ManualVerificationFailed', failedOverrides())
    expect(container?.textContent).toContain('MANUAL VERIFICATION FAILED · USER REPORTED')
    expect(container?.textContent).toContain('failed attempt is immutable')
    const summary = container!.querySelector<HTMLButtonElement>('.verification-case-summary')!
    expect(summary.textContent).toContain('Failed')
    expect(summary.getAttribute('aria-expanded')).toBe('true')
    expect(container!.querySelector('.verification-result-summary')?.textContent).toContain('Observed mismatch')
    expect(container!.querySelectorAll('.verification-document-actions button')).toHaveLength(3)
    await act(async () => root.unmount())
  })

  it('keeps completion controls below the case cards with explicit confirmation and unchanged gates', async () => {
    const view = await render('AwaitingManualVerification', activeOverrides())
    const cases = container!.querySelector('.verification-cases')!
    const completion = container!.querySelector('.verification-completion')!
    expect(cases.compareDocumentPosition(completion) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(completion.textContent).toContain('I confirm these outcomes were recorded by a human.')
    expect([...completion.querySelectorAll<HTMLButtonElement>('button')].every(button => button.disabled)).toBe(true)
    expect(container!.querySelector('.verification-document-actions')).not.toBeNull()
    await act(async () => view.root.unmount())
  })
})
