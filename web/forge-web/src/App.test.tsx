// @vitest-environment jsdom

import { StrictMode } from 'react'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { taskUrl } from './taskNavigation'
import type { EngineeringTask, EngineeringTaskSummary, ImplementationPlan, ModelCall, SystemCapabilities, WorkflowStatus } from './types'

const apiModule = vi.hoisted(() => {
  class MockForgeApiError extends Error {
    code?: string
    constructor(message: string, code?: string) {
      super(message)
      this.code = code
    }
  }

  return {
    ForgeApiError: MockForgeApiError,
    invalidVerificationEligibilityMessage: 'Verification generation state could not be validated. Reload the task before taking action.',
    forgeApi: {
      listTasks: vi.fn(),
      getTask: vi.fn(),
      createTask: vi.fn(),
      answerQuestion: vi.fn(),
      requestRevision: vi.fn(),
      approveRequirement: vi.fn(),
      analyzeRepository: vi.fn(),
      refreshEvidence: vi.fn(),
      createPlan: vi.fn(),
      requestPlanRevision: vi.fn(),
      approvePlan: vi.fn(),
      generateImplementation: vi.fn(),
      approveImplementation: vi.fn(),
      generateVerificationPlan: vi.fn(),
      startVerificationAttempt: vi.fn(),
      generateFailureAnalysis: vi.fn(),
      reconcileFailureAnalysis: vi.fn(),
      approveCorrectionProposal: vi.fn(),
      generateImplementationCorrection: vi.fn(),
      reconcileImplementationCorrection: vi.fn(),
      updateVerificationCase: vi.fn(),
      completeVerification: vi.fn(),
      exportVerificationPlanPdf: vi.fn(),
      exportTaskPdf: vi.fn(),
      exportPlanPdf: vi.fn(),
      getCapabilities: vi.fn(),
    },
  }
})

const taskPdfDownloader = vi.hoisted(() => ({ isActive: false, run: vi.fn() }))
const planPdfDownloader = vi.hoisted(() => ({ isActive: false, run: vi.fn() }))
const requirementCopier = vi.hoisted(() => ({ isPending: false, run: vi.fn() }))

vi.mock('./api', () => apiModule)
vi.mock('./pdfDownload', () => ({
  createTaskPdfDownloader: () => taskPdfDownloader,
  createPlanPdfDownloader: () => planPdfDownloader,
  exportErrorMessage: (caught: unknown) => caught instanceof Error ? caught.message : 'The PDF could not be exported.',
}))
vi.mock('./requirementCopy', () => ({
  createRequirementCopier: () => requirementCopier,
  requirementCopyLabel: (state: string) => state === 'pending' ? 'Copying...' : 'Copy summary',
}))

import App from './App'

const { forgeApi } = apiModule
const firstId = '328cbf18-00ca-4fa8-a64e-3a651fb99079'
const secondId = 'b2ec060f-d030-42f0-bffb-bf3e5402ddb2'
const mountedApps: Array<() => Promise<void>> = []
const capabilities: SystemCapabilities = {
  aiMode: 'Fake',
  clarificationProvider: 'Fake',
  clarificationModel: 'demo',
  reasoningEffort: 'low',
  clarificationConfigured: true,
  planningProvider: 'Fake',
  planningModel: 'demo',
  planningReasoningEffort: 'medium',
  planningConfigured: true,
  implementationProvider: 'Deterministic Fake',
  implementationModel: null,
  implementationReasoningEffort: null,
  implementationConfigured: true,
  aiConfigured: true,
  repositoryInspectionAvailable: true,
  planningAvailable: true,
  targetModificationAvailable: true,
  implementationApprovalAvailable: true,
  implementationCorrectionAvailable: false,
  validationAvailable: false,
  reviewAvailable: true,
  pullRequestCreationAvailable: false,
  fakeImplementationAvailable: true,
  openAiImplementationAvailable: false,
  silentFallbackSupported: false,
  commitAvailable: false,
  pushAvailable: false,
  deliveryPullRequestAvailable: false,
}

describe('App task navigation hardening', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    document.body.innerHTML = ''
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    window.history.replaceState(null, '', '/')
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', {
      configurable: true,
      value(this: HTMLDialogElement) { this.setAttribute('open', '') },
    })
    Object.defineProperty(HTMLDialogElement.prototype, 'close', {
      configurable: true,
      value(this: HTMLDialogElement) {
        this.removeAttribute('open')
        this.dispatchEvent(new Event('close'))
      },
    })
    forgeApi.getCapabilities.mockResolvedValue(capabilities)
    forgeApi.listTasks.mockResolvedValue([])
    forgeApi.getTask.mockRejectedValue(new Error('getTask was not configured for this test.'))
    forgeApi.createTask.mockRejectedValue(new Error('createTask was not configured for this test.'))
    forgeApi.answerQuestion.mockRejectedValue(new Error('answerQuestion was not configured for this test.'))
    forgeApi.requestRevision.mockRejectedValue(new Error('requestRevision was not configured for this test.'))
    forgeApi.approveRequirement.mockRejectedValue(new Error('approveRequirement was not configured for this test.'))
    forgeApi.analyzeRepository.mockRejectedValue(new Error('analyzeRepository was not configured for this test.'))
    forgeApi.refreshEvidence.mockRejectedValue(new Error('refreshEvidence was not configured for this test.'))
    forgeApi.createPlan.mockRejectedValue(new Error('createPlan was not configured for this test.'))
    forgeApi.requestPlanRevision.mockRejectedValue(new Error('requestPlanRevision was not configured for this test.'))
    forgeApi.approvePlan.mockRejectedValue(new Error('approvePlan was not configured for this test.'))
    forgeApi.generateImplementation.mockRejectedValue(new Error('generateImplementation was not configured for this test.'))
    forgeApi.approveImplementation.mockRejectedValue(new Error('approveImplementation was not configured for this test.'))
    forgeApi.generateVerificationPlan.mockRejectedValue(new Error('generateVerificationPlan was not configured for this test.'))
    forgeApi.startVerificationAttempt.mockRejectedValue(new Error('startVerificationAttempt was not configured for this test.'))
    forgeApi.updateVerificationCase.mockRejectedValue(new Error('updateVerificationCase was not configured for this test.'))
    forgeApi.completeVerification.mockRejectedValue(new Error('completeVerification was not configured for this test.'))
    forgeApi.exportVerificationPlanPdf.mockRejectedValue(new Error('exportVerificationPlanPdf was not configured for this test.'))
    taskPdfDownloader.run.mockResolvedValue(undefined)
    planPdfDownloader.run.mockResolvedValue(undefined)
    requirementCopier.run.mockResolvedValue(undefined)
  })

  afterEach(async () => {
    while (mountedApps.length > 0) {
      await mountedApps.pop()!()
    }
    vi.restoreAllMocks()
    document.body.innerHTML = ''
  })

  it('ignores a stale task action after navigation back to the new-task page and clears busy state', async () => {
    const task = buildTask(firstId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary A' })
    const approved = buildTask(firstId, 'ReadyForPlanning')
    const pending = deferred<EngineeringTask>()
    forgeApi.getTask.mockResolvedValue(task)
    forgeApi.approveRequirement.mockReturnValue(pending.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Approve requirement')

    await click(findButton(rendered.container, 'Approve requirement'))
    await click(findButton(rendered.container, 'New task'))
    await expectText(rendered.container, 'What are we building?')

    pending.resolve(approved)
    await settle()

    expect(rendered.container.textContent).toContain('What are we building?')
    expect(rendered.container.textContent).not.toContain('Ready for evidence-backed planning')
    expect(findActionCard(rendered.container).getAttribute('aria-busy')).toBe('false')
  })

  it('shows task history and new task navigation for a Clarifying task', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'Clarifying', {
      currentPendingQuestion: 'Which administrator events should we record?',
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(findButton(rendered.container, 'Task history')).toBeTruthy()
    expect(findButton(rendered.container, 'New task')).toBeTruthy()
    expect(rendered.container.textContent).toContain('Save & reevaluate')
    expect(Array.from(rendered.container.querySelectorAll('button')).some(button => button.textContent?.includes('Generate implementation'))).toBe(false)
  })

  it('shows task history and new task navigation for AwaitingRequirementApproval and keeps approval controls intact', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Approved requirement summary',
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(findButton(rendered.container, 'Task history')).toBeTruthy()
    expect(findButton(rendered.container, 'New task')).toBeTruthy()
    expect(findButton(rendered.container, 'Approve requirement')).toBeTruthy()
    expect(findButton(rendered.container, 'Request correction')).toBeTruthy()
    expect(findButton(rendered.container, 'Copy summary')).toBeTruthy()
  })

  it('shows task history and new task navigation for AwaitingPlanApproval and keeps plan controls intact', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'AwaitingPlanApproval', {
      implementationPlan: buildPlan(),
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(findButton(rendered.container, 'Task history')).toBeTruthy()
    expect(findButton(rendered.container, 'New task')).toBeTruthy()
    expect(findButton(rendered.container, 'Download proposed plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Approve plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Request plan correction')).toBeTruthy()
  })

  it('shows task history and new task navigation for PlanApproved and keeps export controls intact', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'PlanApproved', {
      implementationPlan: buildPlan(),
      planApprovedAt: '2026-07-18T12:05:00.000Z',
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(findButton(rendered.container, 'Task history')).toBeTruthy()
    expect(findButton(rendered.container, 'New task')).toBeTruthy()
    expect(findButton(rendered.container, 'Download approved plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Download task report PDF')).toBeTruthy()
    expect(findButton(rendered.container, 'Generate implementation')).toBeTruthy()
  })

  it('explains incomplete OpenAI configuration without exposing an implementation action', async () => {
    forgeApi.getCapabilities.mockResolvedValue({
      ...capabilities,
      aiMode: 'OpenAI',
      implementationProvider: 'OpenAI',
      implementationConfigured: false,
      fakeImplementationAvailable: false,
      openAiImplementationAvailable: false,
    })
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'PlanApproved', {
      implementationPlan: buildPlan(),
      planApprovedAt: '2026-07-18T12:05:00.000Z',
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(Array.from(rendered.container.querySelectorAll('button')).some(button =>
      button.textContent?.includes('Generate implementation'))).toBe(false)
    expect(rendered.container.textContent).toContain('unavailable until the active provider configuration is complete')
    expect(findButton(rendered.container, 'Download approved plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Download task report PDF')).toBeTruthy()
  })

  it('renders OpenAI billing, pre-worktree validation, and persisted implementation telemetry truthfully', async () => {
    forgeApi.getCapabilities.mockResolvedValue({
      ...capabilities,
      aiMode: 'OpenAI',
      implementationProvider: 'OpenAI',
      implementationModel: 'gpt-5.6-sol',
      implementationReasoningEffort: 'high',
      fakeImplementationAvailable: false,
      openAiImplementationAvailable: true,
    })
    const approved = buildTask(firstId, 'PlanApproved', {
      implementationPlan: buildPlan(),
      planApprovedAt: '2026-07-18T12:05:00.000Z',
    })
    const reviewed = buildImplementationReviewTask(firstId)
    reviewed.implementationResult = {
      ...reviewed.implementationResult!, source: 'OpenAI', model: 'gpt-5.6-sol', isDeterministicFake: false,
      summary: 'OpenAI proposed the approved bounded changes.', warnings: [],
    }
    reviewed.telemetry = {
      totalCalls: 2, usageAvailability: 'Complete', usageUnavailableCallCount: 0,
      totalInputTokens: 100, totalCachedInputTokens: 20, totalOutputTokens: 50, totalReasoningTokens: 10,
      totalEstimatedCostUsd: 0.002, costUnavailableCallCount: 0, isPartialEstimate: false,
      calls: [0, 1].map((index) => ({
        id: `${index + 1}1111111-1111-4111-8111-111111111111`, stage: 'Implementation', provider: 'OpenAI',
        model: 'gpt-5.6-sol', reasoningEffort: 'high', startedAt: '2026-07-18T12:06:00.000Z',
        completedAt: '2026-07-18T12:07:00.000Z', succeeded: index === 1, providerResponseId: index === 1 ? 'response' : null,
        providerRequestId: index === 1 ? 'request' : null, inputTokens: 50, cachedInputTokens: 10,
        uncachedInputTokens: 40, outputTokens: 25, reasoningTokens: 5, estimatedCostUsd: 0.001,
        pricingProvenance: 'stored pricing snapshot', hasStoredPricingSnapshot: true,
        storedPricingSnapshot: { inputPerMillionUsd: 5, cachedInputPerMillionUsd: .5, outputPerMillionUsd: 30 },
        failureCategory: index === 0 ? 'implementation_rate_limit' : null,
      })),
    }
    forgeApi.getTask.mockResolvedValue(approved)
    forgeApi.generateImplementation.mockResolvedValue(reviewed)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    expect(rendered.container.textContent).toContain('OpenAI implementation proposal')
    expect(rendered.container.textContent).toContain('separately billed API usage')
    expect(rendered.container.textContent).toContain('No worktree exists until that validation succeeds')

    await click(findButton(rendered.container, 'Generate implementation'))
    await expectText(rendered.container, 'Deterministic proposal validation: accepted')
    expect(rendered.container.textContent).toContain('OpenAI · gpt-5.6-sol')
    expect(rendered.container.textContent).toContain('Physical calls2')
    expect(rendered.container.textContent).toContain('INPUT TOKENS100')
    expect(rendered.container.textContent).toContain('OUTPUT TOKENS50')
    expect(rendered.container.textContent).toContain('configured estimate')
    expect(rendered.container.textContent).not.toContain('MECHANICAL WORKFLOW DEMONSTRATION')
  })

  it('renders unavailable usage without implying zero tokens or zero cost', async () => {
    const reviewed = buildImplementationReviewTask(firstId)
    reviewed.telemetry = {
      totalCalls: 1, usageAvailability: 'Unavailable', usageUnavailableCallCount: 1,
      totalInputTokens: null, totalCachedInputTokens: null, totalOutputTokens: null, totalReasoningTokens: null,
      totalEstimatedCostUsd: null, costUnavailableCallCount: 1, isPartialEstimate: true,
      calls: [{
        id: '11111111-1111-4111-8111-111111111111', stage: 'Implementation', provider: 'OpenAI',
        model: 'gpt-5.6-sol', reasoningEffort: 'high', startedAt: '2026-07-18T12:06:00.000Z',
        completedAt: '2026-07-18T12:07:00.000Z', succeeded: false, providerResponseId: null,
        providerRequestId: null, usageAvailable: false, inputTokens: null, cachedInputTokens: null,
        uncachedInputTokens: null, outputTokens: null, reasoningTokens: null, estimatedCostUsd: null,
        pricingProvenance: 'cost unavailable', hasStoredPricingSnapshot: true,
        storedPricingSnapshot: { inputPerMillionUsd: 5, cachedInputPerMillionUsd: .5, outputPerMillionUsd: 30 },
        failureCategory: 'implementation_provider_error',
      }],
    }
    forgeApi.getTask.mockResolvedValue(reviewed)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(rendered.container.textContent).toContain('Usage unavailable')
    expect(rendered.container.textContent).toContain('Cost unavailable')
    expect(rendered.container.textContent).not.toContain('Configured est. $0')
    expect(rendered.container.textContent).not.toContain('INPUT TOKENS')
    expect(rendered.container.textContent).not.toContain('OUTPUT TOKENS')
    expect(rendered.container.textContent).not.toContain('$0')
  })

  it('labels mixed usage as partial without presenting known subtotals as totals', async () => {
    const reviewed = buildImplementationReviewTask(firstId)
    reviewed.telemetry = {
      totalCalls: 2, usageAvailability: 'Partial', usageUnavailableCallCount: 1,
      totalInputTokens: null, totalCachedInputTokens: null, totalOutputTokens: null, totalReasoningTokens: null,
      totalEstimatedCostUsd: null, costUnavailableCallCount: 1, isPartialEstimate: true,
      completeEstimatedSubtotalUsd: null, partialEstimatedSubtotalUsd: 0.00026,
      availableEstimatedSubtotalUsd: 0.00026, hasPartialEstimates: true,
      possiblyDispatchedUnavailableEstimatedCostCallCount: 1,
      calls: [buildModelCall('VerificationPlanning', {
        providerUsageAvailability: 'Partial', inputTokens: 12, cachedInputTokens: null,
        uncachedInputTokens: 12, outputTokens: 7, reasoningTokens: null,
        estimatedCostUsd: 0.00026, isPartialEstimate: true,
      }), buildModelCall('Implementation', {
        id: '22222222-2222-4222-8222-222222222222', succeeded: false, providerResponseId: null,
        usageAvailable: false, inputTokens: null, cachedInputTokens: null, uncachedInputTokens: null,
        outputTokens: null, reasoningTokens: null, estimatedCostUsd: null,
        failureCategory: 'implementation_provider_error',
      })],
    }
    forgeApi.getTask.mockResolvedValue(reviewed)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    const text = rendered.container.textContent ?? ''

    expect(text).toContain('Partial usage — aggregate totals unavailable because some calls lack telemetry')
    expect(text).not.toContain('INPUT TOKENS')
    expect(text).not.toContain('OUTPUT TOKENS')
    expect(text).toContain('CONSERVATIVE PARTIAL SUBTOTAL$0.000260')
    expect(text).toContain('AVAILABLE COST SUBTOTAL$0.000260')
    expect(text).toContain('Usage partial')
    expect(text).toContain('Conservative partial est. $0.000260')
    expect(text).toContain('Cost unavailable')
    expect(text).not.toContain('Configured est. $0.000260')
  })

  it('renders complete aggregate usage and preserves explicit provider-reported zero', async () => {
    const reviewed = buildImplementationReviewTask(firstId)
    reviewed.telemetry = {
      totalCalls: 1, usageAvailability: 'Complete', usageUnavailableCallCount: 0,
      totalInputTokens: 0, totalCachedInputTokens: 0, totalOutputTokens: 0, totalReasoningTokens: 0,
      totalEstimatedCostUsd: 0, costUnavailableCallCount: 0, isPartialEstimate: false,
      calls: [buildModelCall('Implementation', {
        usageAvailable: true, inputTokens: 0, cachedInputTokens: 0, uncachedInputTokens: 0,
        outputTokens: 0, reasoningTokens: 0, estimatedCostUsd: 0,
      })],
    }
    forgeApi.getTask.mockResolvedValue(reviewed)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    const text = rendered.container.textContent ?? ''

    expect(text).toContain('INPUT TOKENS0')
    expect(text).toContain('OUTPUT TOKENS0')
    expect(text).toContain('Configured est. $0.000000')
    expect(text).not.toContain('Usage unavailable')
  })

  it('does not convert unavailable planning output usage to zero', async () => {
    const planning = buildTask(firstId, 'Planning')
    planning.telemetry = {
      totalCalls: 1, usageAvailability: 'Unavailable', usageUnavailableCallCount: 1,
      totalInputTokens: null, totalCachedInputTokens: null, totalOutputTokens: null, totalReasoningTokens: null,
      totalEstimatedCostUsd: null, costUnavailableCallCount: 1, isPartialEstimate: true,
      calls: [buildModelCall('Planning', {
        succeeded: false, providerResponseId: null, usageAvailable: false, inputTokens: null,
        cachedInputTokens: null, uncachedInputTokens: null, outputTokens: null, reasoningTokens: null,
        estimatedCostUsd: null, failureCategory: 'output_truncated',
      })],
    }
    forgeApi.getTask.mockResolvedValue(planning)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    const text = rendered.container.textContent ?? ''

    expect(text).toContain('Output token usage unavailable')
    expect(text).not.toContain('0 output tokens')
  })

  it('does not convert unavailable clarification usage to zero', async () => {
    const clarifying = buildTask(firstId, 'Clarifying', { currentPendingQuestion: 'Which format?' })
    clarifying.telemetry = {
      totalCalls: 1, usageAvailability: 'Unavailable', usageUnavailableCallCount: 1,
      totalInputTokens: null, totalCachedInputTokens: null, totalOutputTokens: null, totalReasoningTokens: null,
      totalEstimatedCostUsd: null, costUnavailableCallCount: 1, isPartialEstimate: true,
      calls: [buildModelCall('Clarification', {
        succeeded: false, providerResponseId: null, usageAvailable: false, inputTokens: null,
        cachedInputTokens: null, uncachedInputTokens: null, outputTokens: null, reasoningTokens: null,
        estimatedCostUsd: null, failureCategory: 'provider_error',
      })],
    }
    forgeApi.getTask.mockResolvedValue(clarifying)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    const text = rendered.container.textContent ?? ''

    expect(text).toContain('Usage unavailable')
    expect(text).not.toContain('0 output tokens')
    expect(text).not.toContain('OUTPUT TOKENS0')
  })

  it('generates Fake implementation review and renders truthful bounded diff metadata', async () => {
    const approved = buildTask(firstId, 'PlanApproved', {
      implementationPlan: buildPlan(),
      planApprovedAt: '2026-07-18T12:05:00.000Z',
    })
    const reviewed = buildImplementationReviewTask(firstId)
    forgeApi.getTask.mockResolvedValue(approved)
    forgeApi.generateImplementation.mockResolvedValue(reviewed)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'Generate implementation'))
    await expectText(rendered.container, 'Review the isolated generated changes')

    expect(forgeApi.generateImplementation).toHaveBeenCalledWith(firstId)
    expect(rendered.container.textContent).toContain('MECHANICAL WORKFLOW DEMONSTRATION')
    expect(rendered.container.textContent).toContain('Validation commands were not run.')
    expect(rendered.container.textContent).toContain('The active checkout was verified unchanged.')
    expect(rendered.container.textContent).toContain('Displayed diff is incomplete.')
    expect(rendered.container.textContent).toContain('src/ReportExportService.cs')
    expect(rendered.container.querySelector('pre[aria-label="Unified diff for src/ReportExportService.cs"]')?.textContent)
      .toContain('diff --git')
    expect(findButton(rendered.container, 'Download approved plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Download task report PDF')).toBeTruthy()
  })

  it('confirms and approves the exact persisted implementation revision', async () => {
    const review = buildImplementationReviewTask(firstId)
    const approvedAt = '2026-07-18T12:10:00.000Z'
    const approved: EngineeringTask = {
      ...review,
      status: 'ImplementationApproved',
      rowVersion: review.rowVersion + 1,
      approvedImplementationRevisionId: review.activeImplementationRevisionId,
      implementationRuntime: null,
      implementationRevisions: review.implementationRevisions.map(revision => ({
        ...revision, reviewState: 'Approved', approvedAt, isApproved: true,
      })),
    }
    forgeApi.getTask.mockResolvedValue(review)
    forgeApi.approveImplementation.mockResolvedValue(approved)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'Approve implementation'))
    await settle()

    const dialog = rendered.container.querySelector('dialog')!
    expect(dialog.hasAttribute('open')).toBe(true)
    expect(document.activeElement?.textContent).toBe('Cancel')
    expect(dialog.textContent).toContain('Validation was not run.')
    expect(dialog.textContent).toContain('No files were staged.')
    expect(dialog.textContent).toContain('No commit or push occurred.')
    expect(dialog.textContent).toContain('Approval accepts the persisted review only.')

    await act(async () => { dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })) })
    await settle()
    expect(dialog.hasAttribute('open')).toBe(false)

    await click(findButton(rendered.container, 'Approve implementation'))
    await click(findButton(rendered.container, 'Confirm approval'))
    await expectText(rendered.container, 'Implementation approved')
    expect(forgeApi.approveImplementation).toHaveBeenCalledWith(firstId, expect.objectContaining({
      expectedRowVersion: 7,
      expectedRevisionId: '11111111-1111-4111-8111-111111111111',
      expectedResultFingerprint: 'b'.repeat(64),
      commandId: expect.any(String),
    }))
    expect(rendered.container.textContent).toContain('Revision 1 was approved')
    expect(rendered.container.textContent).toContain('Validation was not run')
    expect(Array.from(rendered.container.querySelectorAll('button')).some(button =>
      button.textContent?.includes('Approve implementation'))).toBe(false)
    expect(findButton(rendered.container, 'Download approved plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Download task report PDF')).toBeTruthy()
  })

  it('submits one approval command when the native dialog confirmation is activated twice rapidly', async () => {
    const review = buildImplementationReviewTask(firstId)
    const pending = deferred<EngineeringTask>()
    const commandId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    const randomUuid = vi.spyOn(crypto, 'randomUUID').mockReturnValue(commandId)
    forgeApi.getTask.mockResolvedValue(review)
    forgeApi.approveImplementation.mockReturnValue(pending.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'Approve implementation'))
    const dialog = rendered.container.querySelector('dialog')!
    const confirm = findButton(dialog, 'Confirm approval')

    await act(async () => {
      confirm.click()
      confirm.click()
    })

    expect(dialog.hasAttribute('open')).toBe(false)
    expect(forgeApi.approveImplementation).toHaveBeenCalledTimes(1)
    expect(randomUuid).toHaveBeenCalledTimes(1)
    expect(forgeApi.approveImplementation).toHaveBeenCalledWith(firstId, expect.objectContaining({ commandId }))

    pending.resolve({
      ...review,
      status: 'ImplementationApproved',
      rowVersion: review.rowVersion + 1,
      approvedImplementationRevisionId: review.activeImplementationRevisionId,
      implementationRevisions: review.implementationRevisions.map(revision => ({
        ...revision,
        reviewState: 'Approved',
        approvedAt: '2026-07-18T12:10:00.000Z',
        isApproved: true,
      })),
    })
    await expectText(rendered.container, 'Implementation approved')
    expect(forgeApi.approveImplementation).toHaveBeenCalledTimes(1)
    expect(rendered.container.textContent).not.toContain('changed while approval was pending')
    randomUuid.mockRestore()
  })

  it('renders the persisted correction candidate as implementation revision two', async () => {
    const review = buildImplementationReviewTask(firstId)
    const revision1 = {
      ...review.implementationRevisions[0], reviewState: 'Approved' as const,
      isCurrent: false, isApproved: true, approvedAt: '2026-07-18T12:10:00.000Z',
    }
    const revision2 = {
      ...review.implementationRevisions[0], revisionId: '99999999-9999-4999-8999-999999999999',
      revisionNumber: 2, kind: 'Correction' as const, previousRevisionId: revision1.revisionId,
      reviewState: 'Current' as const, isCurrent: true, isApproved: false, approvedAt: null,
      resultFingerprint: '9'.repeat(64), correctionSubmittedAt: '2026-07-18T12:11:00.000Z',
      correctionProposalId: '88888888-8888-4888-8888-888888888888',
      correctionProposalFingerprint: '8'.repeat(64),
    }
    review.implementationRevisions = [revision1, revision2]
    review.activeImplementationRevisionId = revision2.revisionId
    review.approvedImplementationRevisionId = revision1.revisionId
    review.implementationResult = {
      ...review.implementationResult!, summary: 'Corrected revision two is ready for exact diff review.',
    }
    review.correctionEligibility = {
      canGenerateFailureAnalysis: false, canApproveCorrection: false, canGenerateCorrection: false,
      canApproveCorrectedRevision: true, canGenerateReplacementVerificationPlan: false,
    }
    forgeApi.getTask.mockResolvedValue(review)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Corrected revision two is ready for exact diff review.')

    expect(rendered.container.textContent).toContain('Revision 2')
    expect(rendered.container.textContent).toContain('CANDIDATE')
    expect(rendered.container.textContent).toContain('Approval accepts revision 2')
    expect(rendered.container.textContent).not.toContain('The previous implementation attempt was interrupted.')
  })

  it('reopens the exact verification-bound implementation diff and returns without another task read', async () => {
    const manual = buildManualVerificationTask(firstId, true)
    const activeAttempt = buildHistoricalManualAttempt(manual)
    manual.currentVerificationAttemptId = activeAttempt.attemptId
    manual.manualVerificationAttempts = [activeAttempt]
    manual.verificationEligibility = { ...manual.verificationEligibility!, canStartVerificationAttempt: false,
      canRecordVerificationResult: true }
    forgeApi.getTask.mockResolvedValue(manual)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Review the exact approved diff used by this verification plan.')
    const notes = [...rendered.container.querySelectorAll('label')].find(label => label.textContent?.startsWith('Notes'))!
      .querySelector<HTMLTextAreaElement>('textarea')!
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!
        .call(notes, 'Unsaved reviewer note.')
      notes.dispatchEvent(new Event('input', { bubbles: true }))
    })
    const reads = forgeApi.getTask.mock.calls.length
    const action = findButton(rendered.container, 'View approved implementation diff')
    const pushes = vi.spyOn(window.history, 'pushState')

    await act(async () => { action.click(); action.click() })

    expect(rendered.container.textContent).toContain('Approved implementation revision 1')
    expect(rendered.container.querySelector('.diff-line-added')?.textContent).toContain('+new')
    expect(rendered.container.querySelector('.diff-line-deleted')?.textContent).toContain('-old')
    expect(window.location.search).toContain('view=implementation')
    expect(forgeApi.getTask).toHaveBeenCalledTimes(reads)
    expect(pushes).toHaveBeenCalledOnce()

    const back = vi.spyOn(window.history, 'back').mockImplementation(() => undefined)
    await click(findButton(rendered.container, 'Back to manual verification'))
    expect(back).toHaveBeenCalledOnce()
    await dispatchPopState(taskUrl(firstId))
    expect(rendered.container.textContent).toContain('Manual verification plan')
    expect(rendered.container.textContent).not.toContain('Approved implementation revision 1')
    expect([...rendered.container.querySelectorAll('label')].find(label => label.textContent?.startsWith('Notes'))!
      .querySelector<HTMLTextAreaElement>('textarea')!.value).toBe('Unsaved reviewer note.')
  })

  it('fails closed when the verification plan fingerprint does not match its revision', async () => {
    const manual = buildManualVerificationTask(firstId, true)
    manual.verificationPlans![0].implementationResultFingerprint = 'f'.repeat(64)
    forgeApi.getTask.mockResolvedValue(manual)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'View approved implementation diff'))

    expect(rendered.container.textContent).toContain('revision binding could not be verified')
    expect(rendered.container.textContent).not.toContain('Approved implementation revision 1')
    expect(window.location.search).not.toContain('view=implementation')
  })

  it('binds replacement verification plan 2 to approved implementation revision 2', async () => {
    const manual = buildManualVerificationTask(firstId, true)
    const revisionId = '77777777-7777-4777-8777-777777777777'
    const fingerprint = '7'.repeat(64)
    manual.activeImplementationRevisionId = revisionId
    manual.approvedImplementationRevisionId = revisionId
    manual.implementationRevisions = [{ ...manual.implementationRevisions[0], revisionId, revisionNumber: 2,
      kind: 'Correction', previousRevisionId: manual.implementationRevisions[0].revisionId, resultFingerprint: fingerprint }]
    manual.verificationPlans = [{ ...manual.verificationPlans![0], planId: '88888888-8888-4888-8888-888888888888',
      planNumber: 2, implementationRevisionId: revisionId, implementationResultFingerprint: fingerprint }]
    manual.currentVerificationPlanId = manual.verificationPlans[0].planId
    forgeApi.getTask.mockResolvedValue(manual)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'View approved implementation diff'))

    expect(rendered.container.textContent).toContain('Approved implementation revision 2')
    expect(window.location.search).toContain(`revision=${revisionId}`)
  })

  it('honors Back and Forward view URLs and clears the review on task switch', async () => {
    const first = buildManualVerificationTask(firstId, true)
    const second = buildTask(secondId, 'Clarifying', { repository: 'C:/repo/second' })
    forgeApi.getTask.mockImplementation(async (id: string) => id === firstId ? first : second)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'View approved implementation diff'))
    const reviewUrl = `${window.location.pathname}${window.location.search}`

    await dispatchPopState(taskUrl(firstId))
    expect(rendered.container.querySelector('.implementation-review-return')).toBeNull()
    await dispatchPopState(reviewUrl)
    expect(rendered.container.textContent).toContain('Approved implementation revision 1')

    await dispatchPopState(taskUrl(secondId))
    await expectText(rendered.container, 'C:/repo/second')
    expect(rendered.container.querySelector('.implementation-review-return')).toBeNull()
    expect(rendered.container.textContent).not.toContain('Approved implementation revision 1')
  })

  it('reopens cached historical plan 1 with revision 1 after replacement plan 2 becomes current', async () => {
    const original = buildManualVerificationTask(firstId, true)
    const revisionTwoId = '77777777-7777-4777-8777-777777777777'
    const revisionTwoFingerprint = '7'.repeat(64)
    const replacement = structuredClone(original)
    replacement.implementationRevisions[0] = { ...replacement.implementationRevisions[0], reviewState: 'HistoricallyApproved', isCurrent: false, isApproved: false }
    replacement.implementationRevisions.push({ ...replacement.implementationRevisions[0], revisionId: revisionTwoId,
      revisionNumber: 2, kind: 'Correction', previousRevisionId: original.implementationRevisions[0].revisionId,
      reviewState: 'Approved', resultFingerprint: revisionTwoFingerprint, isCurrent: true, isApproved: true })
    replacement.verificationPlans![0] = { ...replacement.verificationPlans![0], status: 'Superseded' }
    replacement.verificationPlans!.push({ ...replacement.verificationPlans![0],
      planId: '88888888-8888-4888-8888-888888888888', planNumber: 2, status: 'Current',
      implementationRevisionId: revisionTwoId, implementationResultFingerprint: revisionTwoFingerprint })
    replacement.activeImplementationRevisionId = revisionTwoId
    replacement.approvedImplementationRevisionId = revisionTwoId
    replacement.currentVerificationPlanId = replacement.verificationPlans![1].planId
    const second = buildTask(secondId, 'Clarifying')
    let firstRead = true
    forgeApi.getTask.mockImplementation(async (id: string) => {
      if (id === secondId) return second
      if (firstRead) { firstRead = false; return original }
      return replacement
    })
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await dispatchPopState(taskUrl(secondId))
    await dispatchPopState(taskUrl(firstId))
    await expectText(rendered.container, 'Superseded verification plans')
    const history = rendered.container.querySelector<HTMLDetailsElement>('.verification-plan-history')!
    history.open = true

    await click(history.querySelector<HTMLButtonElement>('button')!)

    expect(rendered.container.textContent).toContain('Approved implementation revision 1')
    expect(window.location.search).toContain(`revision=${original.implementationRevisions[0].revisionId}`)
  })

  it('shows corrected-revision approval progress while the revision-two request is pending', async () => {
    const review = buildImplementationReviewTask(firstId)
    const revision1 = { ...review.implementationRevisions[0], reviewState: 'Approved' as const,
      isCurrent: false, isApproved: true, approvedAt: '2026-07-18T12:08:00.000Z' }
    const revision2 = { ...review.implementationRevisions[0],
      revisionId: '22222222-2222-4222-8222-222222222222', revisionNumber: 2,
      kind: 'Correction' as const, previousRevisionId: revision1.revisionId,
      reviewState: 'Current' as const, resultFingerprint: 'c'.repeat(64),
      correctionSubmittedAt: '2026-07-18T12:09:00.000Z', approvedAt: null,
      isCurrent: true, isApproved: false }
    review.implementationRevisions = [revision1, revision2]
    review.activeImplementationRevisionId = revision2.revisionId
    review.approvedImplementationRevisionId = revision1.revisionId
    const pending = deferred<EngineeringTask>()
    forgeApi.getTask.mockResolvedValue(review)
    forgeApi.approveImplementation.mockReturnValue(pending.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'Approve implementation'))
    await click(findButton(rendered.container, 'Confirm approval'))

    const progress = findButton(rendered.container, 'Approving corrected revision…')
    expect(progress.disabled).toBe(true)
    expect(progress.getAttribute('aria-busy')).toBe('true')
    pending.reject(new Error('Correction approval failed safely.'))
    await settle()
    expect(findButton(rendered.container, 'Approve implementation')).toBeTruthy()
    expect(rendered.container.textContent).toContain('Correction approval failed safely.')
  })

  it('uses an accessible fallback when native dialog support is unavailable', async () => {
    Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal')
    const review = buildImplementationReviewTask(firstId)
    forgeApi.getTask.mockResolvedValue(review)
    forgeApi.approveImplementation.mockResolvedValue({
      ...review,
      status: 'ImplementationApproved',
      rowVersion: review.rowVersion + 1,
      approvedImplementationRevisionId: review.activeImplementationRevisionId,
      implementationRevisions: review.implementationRevisions.map(revision => ({
        ...revision,
        reviewState: 'Approved',
        approvedAt: '2026-07-18T12:10:00.000Z',
        isApproved: true,
      })),
    })

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    expect(rendered.container.querySelector('[role="dialog"]')).toBeNull()

    await click(findButton(rendered.container, 'Approve implementation'))
    await settle()
    const fallback = rendered.container.querySelector('[role="dialog"]') as HTMLElement
    expect(fallback).toBeTruthy()
    expect(fallback.getAttribute('aria-modal')).toBe('true')
    expect(fallback.getAttribute('aria-labelledby')).toBe('implementation-approval-title')
    expect(fallback.getAttribute('aria-describedby')).toBe('implementation-approval-description')
    expect(document.activeElement?.textContent).toBe('Cancel')

    await act(async () => fallback.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })))
    await settle()
    expect(rendered.container.querySelector('[role="dialog"]')).toBeNull()

    await click(findButton(rendered.container, 'Approve implementation'))
    await click(findButton(rendered.container, 'Cancel'))
    expect(rendered.container.querySelector('[role="dialog"]')).toBeNull()

    await click(findButton(rendered.container, 'Approve implementation'))
    await click(findButton(rendered.container, 'Confirm approval'))
    await expectText(rendered.container, 'Implementation approved')
    expect(forgeApi.approveImplementation).toHaveBeenCalledTimes(1)
  })

  it('closes an open approval dialog when task selection changes', async () => {
    const review = buildImplementationReviewTask(firstId)
    forgeApi.getTask.mockResolvedValue(review)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'Approve implementation'))
    expect(rendered.container.querySelector('dialog')?.hasAttribute('open')).toBe(true)

    await click(findButton(rendered.container, 'New task'))
    await settle()

    expect(window.location.pathname).toBe('/')
    expect(rendered.container.querySelector('dialog')).toBeNull()
    expect(rendered.container.textContent).toContain('What are we building?')
  })

  it('reloads and announces a stale implementation approval conflict', async () => {
    const review = buildImplementationReviewTask(firstId)
    const refreshed = buildImplementationReviewTask(firstId)
    refreshed.rowVersion = 8
    refreshed.implementationRevisions[0].resultFingerprint = 'c'.repeat(64)
    forgeApi.getTask.mockResolvedValueOnce(review).mockResolvedValueOnce(refreshed)
    forgeApi.approveImplementation.mockRejectedValue(
      new apiModule.ForgeApiError('The task changed.', 'task_concurrency_conflict'))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'Approve implementation'))
    await click(findButton(rendered.container, 'Confirm approval'))
    await expectText(rendered.container, 'The implementation review changed while approval was pending.')

    expect(forgeApi.getTask).toHaveBeenCalledTimes(2)
    expect(rendered.container.textContent).toContain('cccccccccccc')
  })

  it('does not let a stale approval response restore a task after New task navigation', async () => {
    const review = buildImplementationReviewTask(firstId)
    const pending = deferred<EngineeringTask>()
    forgeApi.getTask.mockResolvedValue(review)
    forgeApi.approveImplementation.mockReturnValue(pending.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'Approve implementation'))
    await click(findButton(rendered.container, 'Confirm approval'))
    await click(findButton(rendered.container, 'New task'))
    pending.resolve({ ...review, status: 'ImplementationApproved' })
    await settle()

    expect(window.location.pathname).toBe('/')
    expect(rendered.container.textContent).toContain('What are we building?')
    expect(rendered.container.textContent).not.toContain('Implementation approved')
  })

  it('renders recoverable implementation failure without claiming changes or validation', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'Implementing', {
      implementationPlan: buildPlan(),
      lastImplementationFailure: {
        category: 'implementation_recovery_required',
        message: 'The isolated workspace requires explicit recovery.',
        recoveryRequired: true,
        occurredAt: '2026-07-18T12:06:00.000Z',
        safeToResume: false,
        activeCheckoutVerified: true,
      },
      implementationRuntime: { workspaceAvailable: true, activeCheckoutVerified: true, disposition: 'RecoveryRequired', safeMessage: 'The isolated workspace requires explicit recovery.' },
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await expectText(rendered.container, 'Workspace recovery is required')
    expect(rendered.container.textContent).toContain('will not reset, delete, or overwrite')
    expect(rendered.container.textContent).not.toContain('Validation succeeded')
    expect(findButton(rendered.container, 'Task history')).toBeTruthy()
    expect(findButton(rendered.container, 'New task')).toBeTruthy()
  })

  it('renders truthful Implementing progress without completing validation or offering downstream actions', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'Implementing', {
      implementationPlan: buildPlan(),
      implementationWorkspace: {
        branch: `forge/task-${firstId.replaceAll('-', '')}`,
        baseCommitSha: '0123456789abcdef0123456789abcdef01234567',
        phase: 'Ready',
        createdAt: '2026-07-18T12:06:00.000Z',
        updatedAt: '2026-07-18T12:06:00.000Z',
        isAvailable: true,
      },
      implementationRuntime: { workspaceAvailable: true, activeCheckoutVerified: true, disposition: 'Active', safeMessage: null },
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await expectText(rendered.container, 'Preparing generated changes for diff review')
    expect(rendered.container.textContent).toContain('valid implementation lease')
    expect(rendered.container.textContent).not.toContain('Validation succeeded')
    expect(Array.from(rendered.container.querySelectorAll('button')).some(button =>
      /validate|commit|push|pull request/i.test(button.textContent ?? ''))).toBe(false)
  })

  it('offers Resume only for a proven safe-resume disposition', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'Implementing', {
      implementationPlan: buildPlan(),
      planApprovedAt: '2026-07-18T12:05:00.000Z',
      implementationRuntime: {
        workspaceAvailable: true,
        activeCheckoutVerified: true,
        disposition: 'SafeResume',
        safeMessage: 'The untouched reserved workspace was verified.',
      },
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await expectText(rendered.container, 'The isolated workspace can be safely resumed')
    expect(findButton(rendered.container, 'Resume implementation')).toBeTruthy()
    expect(rendered.container.querySelector('[role="status"] .spinner')).toBeNull()
  })

  it('renders expired interrupted and terminal attempts without an active spinner or Resume', async () => {
    const interrupted = buildTask(firstId, 'Implementing', {
      implementationPlan: buildPlan(),
      planApprovedAt: '2026-07-18T12:05:00.000Z',
      implementationRuntime: {
        workspaceAvailable: true,
        activeCheckoutVerified: true,
        disposition: 'RecoveryRequired',
        safeMessage: 'The implementation lease expired during workspace preparation.',
      },
    })
    forgeApi.getTask.mockResolvedValueOnce(interrupted)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Workspace recovery is required')
    expect(Array.from(rendered.container.querySelectorAll('button')).some(button => button.textContent?.includes('Resume implementation'))).toBe(false)
    await rendered.unmount()

    forgeApi.getTask.mockResolvedValue(buildTask(secondId, 'Implementing', {
      implementationPlan: buildPlan(),
      planApprovedAt: '2026-07-18T12:05:00.000Z',
      implementationRuntime: {
        workspaceAvailable: false,
        activeCheckoutVerified: true,
        disposition: 'TerminalIncompatible',
        safeMessage: 'The approved file action is not supported by deterministic Fake mode.',
      },
    }))
    await navigate(taskUrl(secondId))
    const terminal = await renderApp()
    await expectText(terminal.container, 'not compatible with Fake implementation')
    expect(Array.from(terminal.container.querySelectorAll('button')).some(button => button.textContent?.includes('Resume implementation'))).toBe(false)
  })

  it('does not claim an unchanged active checkout when the postcondition is uncertain', async () => {
    const review = buildImplementationReviewTask(firstId)
    review.implementationResult!.activeCheckoutVerified = false
    review.implementationRuntime = {
      workspaceAvailable: true,
      activeCheckoutVerified: false,
      disposition: 'RecoveryRequired',
      safeMessage: 'The active checkout postcondition is uncertain.',
    }
    review.lastImplementationFailure = {
      category: 'implementation_active_checkout_uncertain',
      message: 'The active checkout postcondition is uncertain.',
      recoveryRequired: true,
      occurredAt: review.updatedAt,
      safeToResume: false,
      activeCheckoutVerified: false,
    }
    forgeApi.getTask.mockResolvedValue(review)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await expectText(rendered.container, 'This review cannot be approved.')
    expect(rendered.container.textContent).toContain('could not verify that the active checkout remained unchanged')
    expect(rendered.container.textContent).not.toContain('The active checkout was verified unchanged.')
    expect(Array.from(rendered.container.querySelectorAll('button')).some(button =>
      button.textContent?.includes('Approve implementation'))).toBe(false)
  })

  it.each(['Validating', 'Reviewing', 'Completed'] as const)('keeps semantic approved-plan downloads in %s', async status => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, status, {
      implementationPlan: buildPlan(),
      planApprovedAt: '2026-07-18T12:05:00.000Z',
    }))
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(findButton(rendered.container, 'Download approved plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Download task report PDF')).toBeTruthy()
  })

  it('groups later verification documents once without a duplicate approved-plan section', async () => {
    const manual = buildManualVerificationTask(firstId, true)
    manual.implementationPlan = buildPlan()
    manual.planApprovedAt = '2026-07-18T12:05:00.000Z'
    forgeApi.getTask.mockResolvedValue(manual)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(rendered.container.textContent).not.toContain('Approved plan documents')
    expect([...rendered.container.querySelectorAll('button')].filter(button => button.textContent === 'Approved plan PDF')).toHaveLength(1)
    expect([...rendered.container.querySelectorAll('button')].filter(button => button.textContent === 'Verification plan PDF')).toHaveLength(1)
    expect([...rendered.container.querySelectorAll('button')].filter(button => button.textContent === 'Task report PDF')).toHaveLength(1)
  })

  it('does not let a stale implementation response restore a task after New task navigation', async () => {
    const approved = buildTask(firstId, 'PlanApproved', { implementationPlan: buildPlan() })
    const pending = deferred<EngineeringTask>()
    forgeApi.getTask.mockResolvedValue(approved)
    forgeApi.generateImplementation.mockReturnValue(pending.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'Generate implementation'))
    await click(findButton(rendered.container, 'New task'))
    pending.resolve(buildImplementationReviewTask(firstId))
    await settle()

    expect(window.location.pathname).toBe('/')
    expect(window.location.search).toBe('')
    expect(rendered.container.textContent).toContain('What are we building?')
    expect(rendered.container.textContent).not.toContain('Review the isolated generated changes')
  })

  it('keeps task B selected when task A resolves after the user navigates to another task', async () => {
    const taskA = buildTask(firstId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary A', repository: 'C:/repo/A' })
    const taskB = buildTask(secondId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary B', repository: 'C:/repo/B' })
    const approvedA = buildTask(firstId, 'ReadyForPlanning', { repository: 'C:/repo/A' })
    const pending = deferred<EngineeringTask>()
    forgeApi.getTask.mockImplementation((id: string) => Promise.resolve(id === firstId ? taskA : taskB))
    forgeApi.approveRequirement.mockReturnValue(pending.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Approve summary A')

    await click(findButton(rendered.container, 'Approve requirement'))
    await navigate(taskUrl(secondId))
    await expectText(rendered.container, 'Approve summary B')

    pending.resolve(approvedA)
    await settle()

    expect(rendered.container.textContent).toContain('Approve summary B')
    expect(rendered.container.textContent).toContain('C:/repo/B')
    expect(rendered.container.textContent).not.toContain('Ready for evidence-backed planning')
  })

  it('opens task history from a non-approved task, identifies the selected item, switches tasks, updates the URL, and closes the panel', async () => {
    const taskA = buildTask(firstId, 'Clarifying', {
      currentPendingQuestion: 'Which administrator events should we record?',
      repository: 'C:/repo/A',
    })
    const taskB = buildTask(secondId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Approve summary B',
      repository: 'C:/repo/B',
    })
    forgeApi.getTask.mockImplementation((id: string) => Promise.resolve(id === firstId ? taskA : taskB))
    forgeApi.listTasks.mockResolvedValue([
      buildSummary(taskA, 'Recent question'),
      buildSummary(taskB, 'Recent summary'),
    ])

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'Task history'))
    await expectText(rendered.container, 'Recent tasks')
    expect(findButton(rendered.container, 'Task history').getAttribute('aria-expanded')).toBe('true')
    expect(findSelectedHistoryButton(rendered.container).textContent).toContain('C:/repo/A')

    await click(findHistoryButton(rendered.container, 'C:/repo/B'))
    await expectText(rendered.container, 'Approve summary B')

    expect(window.location.search).toBe(`?task=${secondId}`)
    expect(findButton(rendered.container, 'Task history').getAttribute('aria-expanded')).toBe('false')
    expect(rendered.container.textContent).not.toContain('Recent question')
  })

  it('opens a new task from a non-approved task without mutating persisted task data', async () => {
    const task = buildTask(firstId, 'Clarifying', {
      currentPendingQuestion: 'Which administrator events should we record?',
    })
    forgeApi.getTask.mockResolvedValue(task)
    forgeApi.listTasks.mockResolvedValue([buildSummary(task, 'Recent question')])

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'New task'))
    await expectText(rendered.container, 'What are we building?')
    await expectText(rendered.container, 'Recent tasks')

    expect(window.location.pathname).toBe('/')
    expect(window.location.search).toBe('')
    expect(forgeApi.answerQuestion).not.toHaveBeenCalled()
    expect(forgeApi.requestRevision).not.toHaveBeenCalled()
    expect(forgeApi.approveRequirement).not.toHaveBeenCalled()
    expect(forgeApi.analyzeRepository).not.toHaveBeenCalled()
    expect(forgeApi.refreshEvidence).not.toHaveBeenCalled()
    expect(forgeApi.createPlan).not.toHaveBeenCalled()
    expect(forgeApi.requestPlanRevision).not.toHaveBeenCalled()
    expect(forgeApi.approvePlan).not.toHaveBeenCalled()
  })

  it('keeps the current task error when an older malformed decoded response fails later', async () => {
    const taskA = buildTask(firstId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary A' })
    const taskB = buildTask(secondId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary B' })
    const staleFailure = deferred<EngineeringTask>()
    const currentFailure = deferred<EngineeringTask>()
    forgeApi.getTask.mockImplementation((id: string) => Promise.resolve(id === firstId ? taskA : taskB))
    forgeApi.approveRequirement
      .mockReturnValueOnce(staleFailure.promise)
      .mockReturnValueOnce(currentFailure.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Approve summary A')

    await click(findButton(rendered.container, 'Approve requirement'))
    await navigate(taskUrl(secondId))
    await expectText(rendered.container, 'Approve summary B')
    await click(findButton(rendered.container, 'Approve requirement'))

    currentFailure.reject(new Error('Current task failure'))
    await settle()
    expect(rendered.container.textContent).toContain('Current task failure')

    staleFailure.reject(new Error('The task response could not be validated safely.'))
    await settle()

    expect(rendered.container.textContent).toContain('Current task failure')
    expect(rendered.container.textContent).not.toContain('could not be validated safely')
  })

  it('shows and clears replacement-plan progress for approved revision two', async () => {
    const selected = buildVerificationReadyTask(firstId)
    const revision1 = { ...selected.implementationRevisions[0], reviewState: 'HistoricallyApproved' as const,
      isApproved: false, isCurrent: false }
    const revision2 = { ...selected.implementationRevisions[0],
      revisionId: '99999999-9999-4999-8999-999999999999', revisionNumber: 2,
      kind: 'Correction' as const, previousRevisionId: revision1.revisionId,
      reviewState: 'Approved' as const, resultFingerprint: '9'.repeat(64), isApproved: true, isCurrent: true }
    selected.implementationRevisions = [revision1, revision2]
    selected.activeImplementationRevisionId = revision2.revisionId
    selected.approvedImplementationRevisionId = revision2.revisionId
    selected.verificationEligibility = {
      ...selected.verificationEligibility!, isInitialVerificationPlanGeneration: false,
    }
    const pending = deferred<EngineeringTask>()
    forgeApi.getTask.mockResolvedValue(selected)
    forgeApi.generateVerificationPlan.mockReturnValue(pending.promise)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'Generate replacement verification plan'))
    const progress = findButton(rendered.container, 'Generating verification plan 2…')
    expect(progress.disabled).toBe(true)
    expect(progress.getAttribute('aria-busy')).toBe('true')
    pending.resolve(selected)
    await settle()
    expect(findButton(rendered.container, 'Generate replacement verification plan')).toBeTruthy()
  })

  it.each([
    ['valid', false],
    ['malformed', true],
  ])('discards a stale %s verification recovery after a newer task is selected', async (_label, malformed) => {
    const taskA = buildVerificationReadyTask(firstId)
    const taskB = buildTask(secondId, 'AwaitingRequirementApproval', { requirementSummary: 'Current task B' })
    const recovery = deferred<EngineeringTask>()
    let firstTaskLoads = 0
    forgeApi.getTask.mockImplementation((id: string) => {
      if (id === secondId) return Promise.resolve(taskB)
      firstTaskLoads += 1
      return firstTaskLoads === 1 ? Promise.resolve(taskA) : recovery.promise
    })
    forgeApi.generateVerificationPlan.mockRejectedValue(new Error('Old verification generation failed.'))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Generate manual verification plan')
    await click(findButton(rendered.container, 'Generate manual verification plan'))
    await settle()
    expect(firstTaskLoads).toBe(2)

    await navigate(taskUrl(secondId))
    await expectText(rendered.container, 'Current task B')
    if (malformed) recovery.reject(new Error('The task response could not be validated safely.'))
    else recovery.resolve(taskA)
    await settle()

    expect(rendered.container.textContent).toContain('Current task B')
    expect(rendered.container.textContent).not.toContain('Old verification generation failed.')
    expect(rendered.container.textContent).not.toContain('could not be validated safely')
    expect(forgeApi.generateVerificationPlan).toHaveBeenCalledTimes(1)
  })

  it('shows a bounded load error and enables no mutation after malformed full-task JSON is rejected', async () => {
    forgeApi.getTask.mockRejectedValue(new Error('The task response could not be validated safely.'))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'The requested task could not be loaded.')

    expect(rendered.container.textContent).not.toContain('Generate manual verification plan')
    expect(forgeApi.generateVerificationPlan).not.toHaveBeenCalled()
    expect(forgeApi.startVerificationAttempt).not.toHaveBeenCalled()
    expect(forgeApi.updateVerificationCase).not.toHaveBeenCalled()
    expect(forgeApi.completeVerification).not.toHaveBeenCalled()
    expect(rendered.container.textContent).not.toContain('The task response could not be validated safely.')
  })

  it('rechecks the latest eligibility before a stale Start handler can dispatch', async () => {
    const eligible = buildManualVerificationTask(firstId, true)
    const ineligible = buildManualVerificationTask(firstId, false)
    forgeApi.getTask.mockResolvedValueOnce(eligible).mockResolvedValueOnce(ineligible)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    const initialButton = findButton(rendered.container, 'Start verification')
    expect(initialButton.disabled).toBe(false)

    await navigate(taskUrl(firstId))
    await settle()
    const currentButton = findButton(rendered.container, 'Start verification')
    expect(currentButton.disabled).toBe(true)
    await act(async () => initialButton.dispatchEvent(new MouseEvent('click', { bubbles: true })))
    await act(async () => currentButton.dispatchEvent(new MouseEvent('click', { bubbles: true })))

    expect(forgeApi.startVerificationAttempt).not.toHaveBeenCalled()
  })

  it('rejects a stale Start handler when attempt history appears without a current pointer', async () => {
    const eligible = buildManualVerificationTask(firstId, true)
    const contradictory = { ...eligible, manualVerificationAttempts: [buildHistoricalManualAttempt(eligible)] }
    forgeApi.getTask.mockResolvedValueOnce(eligible).mockResolvedValueOnce(contradictory)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    const staleButton = findButton(rendered.container, 'Start verification')
    await navigate(taskUrl(firstId))
    await settle()
    const currentButton = findButton(rendered.container, 'Start verification')
    expect(currentButton.disabled).toBe(true)
    await act(async () => {
      currentButton.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
      staleButton.dispatchEvent(new MouseEvent('click', { bubbles: true }))
      currentButton.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })

    expect(forgeApi.startVerificationAttempt).not.toHaveBeenCalled()
    expect(rendered.container.textContent).toContain('Manual verification plan.')
  })

  it('preserves the last valid manual task when a mutation response fails decoding', async () => {
    const eligible = buildManualVerificationTask(firstId, true)
    forgeApi.getTask.mockResolvedValue(eligible)
    forgeApi.startVerificationAttempt.mockRejectedValue(
      new Error('The task response could not be validated safely. Reload the task before taking action.'))
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await click(findButton(rendered.container, 'Start verification'))
    await settle()

    expect(rendered.container.textContent).toContain('Manual verification plan.')
    expect(rendered.container.textContent).toContain('could not be validated safely. Reload the task')
    expect(forgeApi.startVerificationAttempt).toHaveBeenCalledTimes(1)
    expect(forgeApi.updateVerificationCase).not.toHaveBeenCalled()
    expect(forgeApi.completeVerification).not.toHaveBeenCalled()
  })

  it('dispatches at most one Start request for rapid activation', async () => {
    const eligible = buildManualVerificationTask(firstId, true)
    const pending = deferred<EngineeringTask>()
    forgeApi.getTask.mockResolvedValue(eligible)
    forgeApi.startVerificationAttempt.mockReturnValue(pending.promise)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    const start = findButton(rendered.container, 'Start verification')
    await act(async () => {
      start.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
      start.click()
      start.click()
    })
    expect(forgeApi.startVerificationAttempt).toHaveBeenCalledTimes(1)
    const pendingButton = findButton(rendered.container, 'Starting verification…')
    expect(pendingButton.disabled).toBe(true)
    expect(pendingButton.getAttribute('aria-busy')).toBe('true')

    await click(findButton(rendered.container, 'New task'))
    pending.resolve(eligible)
    await settle()
    expect(rendered.container.textContent).toContain('What are we building?')
    expect(forgeApi.startVerificationAttempt).toHaveBeenCalledTimes(1)
  })

  it('clears Start progress after success and bounded error', async () => {
    const eligible = buildManualVerificationTask(firstId, true)
    const first = deferred<EngineeringTask>()
    const second = deferred<EngineeringTask>()
    forgeApi.getTask.mockResolvedValue(eligible)
    forgeApi.startVerificationAttempt.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'Start verification'))
    expect(findButton(rendered.container, 'Starting verification…')).toBeTruthy()
    first.resolve(eligible)
    await settle()
    expect(findButton(rendered.container, 'Start verification').getAttribute('aria-busy')).toBe('false')

    await click(findButton(rendered.container, 'Start verification'))
    second.reject(new Error('The verification attempt could not be started safely.'))
    await settle()
    expect(findButton(rendered.container, 'Start verification')).toBeTruthy()
    expect(rendered.container.textContent).toContain('could not be started safely')
  })

  it('never treats the latest plan history entry as actionable without a current pointer', async () => {
    const historicalOnly = { ...buildManualVerificationTask(firstId, false), currentVerificationPlanId: null }
    forgeApi.getTask.mockResolvedValue(historicalOnly)
    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await settle()

    expect(rendered.container.textContent).not.toContain('Manual verification plan.')
    expect(Array.from(rendered.container.querySelectorAll('button')).some(button =>
      button.textContent === 'Start verification')).toBe(false)
    expect(forgeApi.startVerificationAttempt).not.toHaveBeenCalled()
  })

  it('still applies the result for the current selection action', async () => {
    const task = buildTask(firstId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary A' })
    const approved = buildTask(firstId, 'ReadyForPlanning')
    forgeApi.getTask.mockResolvedValue(task)
    forgeApi.approveRequirement.mockResolvedValue(approved)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Approve summary A')

    await click(findButton(rendered.container, 'Approve requirement'))
    await expectText(rendered.container, 'Ready for evidence-backed planning')

    expect(rendered.container.textContent).toContain('Analyze repository and create plan')
  })

  it('preserves Back and Forward task navigation after switching through task history', async () => {
    const taskA = buildTask(firstId, 'Clarifying', {
      currentPendingQuestion: 'Question A',
      repository: 'C:/repo/A',
    })
    const taskB = buildTask(secondId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Approve summary B',
      repository: 'C:/repo/B',
    })
    forgeApi.getTask.mockImplementation((id: string) => Promise.resolve(id === firstId ? taskA : taskB))
    forgeApi.listTasks.mockResolvedValue([
      buildSummary(taskA, 'Question A'),
      buildSummary(taskB, 'Summary B'),
    ])

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'Task history'))
    await click(findHistoryButton(rendered.container, 'C:/repo/B'))
    await expectText(rendered.container, 'Approve summary B')

    await dispatchPopState(taskUrl(firstId))
    await expectText(rendered.container, 'Question A')

    await dispatchPopState(taskUrl(secondId))
    await expectText(rendered.container, 'Approve summary B')
  })

  it('restores a deep-linked task safely under StrictMode and removes the popstate listener on cleanup', async () => {
    const staleSelection = buildTask(firstId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Stale summary',
      repository: 'C:/repo/stale',
    })
    const currentSelection = buildTask(firstId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Current summary',
      repository: 'C:/repo/current',
    })
    const capabilityRequests: Array<ReturnType<typeof deferred<SystemCapabilities>>> = []
    const taskRequests: Array<ReturnType<typeof deferred<EngineeringTask>>> = []
    forgeApi.getCapabilities.mockImplementation(() => {
      const request = deferred<SystemCapabilities>()
      capabilityRequests.push(request)
      return request.promise
    })
    forgeApi.getTask.mockImplementation(() => {
      const request = deferred<EngineeringTask>()
      taskRequests.push(request)
      return request.promise
    })

    await navigate(taskUrl(firstId))
    const pushState = vi.spyOn(window.history, 'pushState')
    const rendered = await renderApp(true)

    expect(forgeApi.getCapabilities.mock.calls.length).toBeGreaterThanOrEqual(2)
    expect(forgeApi.getTask.mock.calls.length).toBeGreaterThanOrEqual(2)

    capabilityRequests.at(-1)!.resolve(capabilities)
    taskRequests.at(-1)!.resolve(currentSelection)
    await settle()

    capabilityRequests.slice(0, -1).forEach(request => request.reject(new Error('stale capabilities failure')))
    taskRequests.slice(0, -1).forEach(request => request.resolve(staleSelection))
    await settle()

    expect(rendered.container.textContent).toContain('Current summary')
    expect(rendered.container.textContent).toContain('C:/repo/current')
    expect(rendered.container.textContent).not.toContain('Stale summary')
    expect(rendered.container.textContent).not.toContain('Provider status unavailable')
    expect(pushState).not.toHaveBeenCalled()

    const callsBeforeUnmount = forgeApi.getTask.mock.calls.length
    await rendered.unmount()
    await navigate(taskUrl(secondId))
    expect(forgeApi.getTask).toHaveBeenCalledTimes(callsBeforeUnmount)
  })
})

async function renderApp(strictMode = false) {
  const container = document.createElement('div')
  document.body.appendChild(container)
  const root = createRoot(container)
  await act(async () => {
    root.render(strictMode
      ? <StrictMode><App /></StrictMode>
      : <App />)
  })

  const unmount = async () => {
    const index = mountedApps.indexOf(unmount)
    if (index >= 0) mountedApps.splice(index, 1)
    await act(async () => {
      root.unmount()
    })
    container.remove()
  }
  mountedApps.push(unmount)

  return {
    container,
    unmount,
  }
}

async function click(button: HTMLButtonElement) {
  await act(async () => {
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

async function navigate(url: string) {
  await act(async () => {
    window.history.pushState(null, '', url)
    window.dispatchEvent(new PopStateEvent('popstate'))
  })
}

async function expectText(container: HTMLElement, text: string) {
  await settle()
  expect(container.textContent).toContain(text)
}

async function settle() {
  await act(async () => {
    await Promise.resolve()
  })
}

async function dispatchPopState(url: string) {
  await act(async () => {
    window.history.replaceState(null, '', url)
    window.dispatchEvent(new PopStateEvent('popstate'))
  })
}

function findButton(container: HTMLElement, label: string) {
  const button = Array.from(container.querySelectorAll('button'))
    .find(candidate => candidate.textContent?.includes(label))
  expect(button).toBeTruthy()
  return button as HTMLButtonElement
}

function findHistoryButton(container: HTMLElement, repository: string) {
  const button = Array.from(container.querySelectorAll('.task-list button'))
    .find(candidate => candidate.textContent?.includes(repository))
  expect(button).toBeTruthy()
  return button as HTMLButtonElement
}

function findSelectedHistoryButton(container: HTMLElement) {
  const button = container.querySelector('.task-list button[aria-current="page"]')
  expect(button).toBeTruthy()
  return button as HTMLButtonElement
}

function findActionCard(container: HTMLElement) {
  const card = container.querySelector('.action-card')
  expect(card).toBeTruthy()
  return card as HTMLElement
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })

  return { promise, resolve, reject }
}

function buildModelCall(stage: ModelCall['stage'], overrides: Partial<ModelCall> = {}): ModelCall {
  return {
    id: '11111111-1111-4111-8111-111111111111', stage, provider: 'OpenAI', model: 'gpt-5.6-sol',
    reasoningEffort: 'high', startedAt: '2026-07-18T12:06:00.000Z', completedAt: '2026-07-18T12:07:00.000Z',
    succeeded: true, providerResponseId: 'response-safe', providerRequestId: 'request-safe', usageAvailable: true,
    inputTokens: 100, cachedInputTokens: 20, uncachedInputTokens: 80, outputTokens: 30, reasoningTokens: 5,
    estimatedCostUsd: 0.001, pricingProvenance: 'stored pricing snapshot', hasStoredPricingSnapshot: true,
    storedPricingSnapshot: { inputPerMillionUsd: 5, cachedInputPerMillionUsd: .5, outputPerMillionUsd: 30 },
    failureCategory: null,
    ...overrides,
  }
}

function buildTask(id: string, status: WorkflowStatus, overrides: Partial<EngineeringTask> = {}): EngineeringTask {
  const now = '2026-07-18T12:00:00.000Z'
  return {
    id,
    repository: `C:/repo/${id.slice(0, 6)}`,
    originalRequirement: `Requirement ${id}`,
    currentClarifiedRequirement: `Requirement ${id}`,
    clarificationAnswers: [],
    requirementRevisionNotes: [],
    planRevisionNotes: [],
    currentPendingQuestion: null,
    requirementSummary: status === 'AwaitingRequirementApproval' ? `Summary ${id}` : null,
    status,
    createdAt: now,
    updatedAt: now,
    requirementApprovedAt: status === 'ReadyForPlanning' ? now : null,
    planApprovedAt: null,
    repositorySnapshot: null,
    evidenceItems: [],
    evidenceFilesInspected: 0,
    evidenceFilesSelected: 0,
    totalEvidenceCharacters: 0,
    implementationPlan: null,
    repositoryAnalyzedAt: null,
    repositoryFingerprint: null,
    planCreatedAt: null,
    implementationWorkspace: null,
    implementationResult: null,
    lastImplementationFailure: null,
    implementationStartedAt: null,
    implementationCompletedAt: null,
    implementationRuntime: null,
    rowVersion: 1,
    activeImplementationRevisionId: null,
    approvedImplementationRevisionId: null,
    implementationRevisions: [],
    telemetry: {
      totalCalls: 0,
      usageAvailability: 'Complete',
      usageUnavailableCallCount: 0,
      totalInputTokens: 0,
      totalCachedInputTokens: 0,
      totalOutputTokens: 0,
      totalReasoningTokens: 0,
      totalEstimatedCostUsd: 0,
      costUnavailableCallCount: 0,
      isPartialEstimate: false,
      calls: [],
    },
    ...overrides,
  }
}

function buildVerificationReadyTask(id: string): EngineeringTask {
  const revisionId = '22222222-2222-4222-8222-222222222222'
  return buildTask(id, 'ImplementationApproved', {
    approvedImplementationRevisionId: revisionId,
    implementationRevisions: [{
      revisionId, revisionNumber: 1, kind: 'Initial', previousRevisionId: null,
      planFingerprint: 'a'.repeat(64), baseCommitSha: 'b'.repeat(40),
      generationStartedAt: '2026-07-18T11:00:00.000Z', generationCompletedAt: '2026-07-18T11:10:00.000Z',
      generationState: 'Succeeded', reviewState: 'Approved', failureCategory: null, failureMessage: null,
      resultFingerprint: 'c'.repeat(64), changedFileCount: 1, correctionSubmittedAt: null,
      approvedAt: '2026-07-18T11:20:00.000Z', isCurrent: false, isApproved: true,
    }],
    verificationPlans: [], verificationPlanGenerationAttempts: [], manualVerificationAttempts: [],
    currentVerificationPlanId: null, currentVerificationAttemptId: null,
    verificationEligibility: {
      canGenerateVerificationPlan: true, canStartVerificationAttempt: false, canRecordVerificationResult: false,
      canCompleteVerificationPassed: false, canCompleteVerificationFailed: false, readyForDelivery: false,
      ineligibilityReason: null, isInitialVerificationPlanGeneration: true,
      canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'NotStarted',
      verificationGenerationStatusMessage: 'Verification-plan generation is ready to start.',
    },
  })
}

function buildManualVerificationTask(id: string, canStart: boolean): EngineeringTask {
  const revisionId = '22222222-2222-4222-8222-222222222222'
  const resultFingerprint = 'c'.repeat(64)
  const planId = '11111111-1111-4111-8111-111111111111'
  return buildTask(id, 'AwaitingManualVerification', {
    implementationResult: buildImplementationReviewTask(id).implementationResult,
    activeImplementationRevisionId: revisionId,
    approvedImplementationRevisionId: revisionId,
    implementationRevisions: [{
      revisionId, revisionNumber: 1, kind: 'Initial', previousRevisionId: null,
      planFingerprint: 'a'.repeat(64), baseCommitSha: '0123456789abcdef0123456789abcdef01234567',
      generationStartedAt: '2026-07-18T11:00:00.000Z', generationCompletedAt: '2026-07-18T11:10:00.000Z',
      generationState: 'Succeeded', reviewState: 'Approved', failureCategory: null, failureMessage: null,
      resultFingerprint, changedFileCount: 1, correctionSubmittedAt: null,
      approvedAt: '2026-07-18T11:20:00.000Z', isCurrent: true, isApproved: true,
    }],
    currentVerificationPlanId: planId,
    currentVerificationAttemptId: null,
    verificationPlans: [{
      planId, planNumber: 1, implementationRevisionId: revisionId, implementationResultFingerprint: resultFingerprint,
      approvedRequirementFingerprint: 'd'.repeat(64), approvedPlanFingerprint: 'e'.repeat(64),
      generationContextFingerprint: 'f'.repeat(64), generatedAt: '2026-07-18T11:30:00.000Z',
      source: 'DeterministicFake', model: null, reasoningEffort: null, summary: 'Manual verification plan.',
      scope: 'Approved revision.', preconditions: [], testCases: [{
        testCaseId: '33333333-3333-4333-8333-333333333333', order: 1, title: 'Manual check',
        objective: 'Inspect safely.', category: 'ManualBehavior', isRequired: true, preconditions: [], testData: [],
        orderedSteps: [{ order: 1, instruction: 'Inspect manually.', approvedValidationCommandId: null,
          expectedObservation: 'Expected behavior.' }], expectedResult: 'Expected behavior.', negativeOrEdgeCases: [],
        regressionScope: [], evidenceRequirements: [], safetyNotes: [],
      }], risks: [], limitations: [], evidenceGuidance: [], planFingerprint: '9'.repeat(64), status: 'Current',
      trustLabel: 'FORGE GENERATED', executionLabel: 'MANUAL — NOT EXECUTED BY FORGE',
    }],
    verificationPlanGenerationAttempts: [], manualVerificationAttempts: [],
    verificationEligibility: {
      canGenerateVerificationPlan: false, canStartVerificationAttempt: canStart,
      canRecordVerificationResult: false, canCompleteVerificationPassed: false,
      canCompleteVerificationFailed: false, readyForDelivery: false, ineligibilityReason: null,
      isInitialVerificationPlanGeneration: false, canRetryVerificationPlanGeneration: false,
      verificationGenerationStatus: 'Completed', verificationGenerationStatusMessage: 'Verification plan ready.',
    },
  })
}

function buildHistoricalManualAttempt(task: EngineeringTask) {
  const plan = task.verificationPlans![0]
  return {
    attemptId: '55555555-5555-4555-8555-555555555555', attemptNumber: 1,
    verificationPlanId: plan.planId, verificationPlanFingerprint: plan.planFingerprint,
    implementationRevisionId: plan.implementationRevisionId,
    implementationResultFingerprint: plan.implementationResultFingerprint,
    startedAt: '2026-07-18T11:40:00.000Z', completedAt: null, status: 'InProgress' as const,
    resultRevisions: [], currentCaseResults: [], completionConfirmation: null, summary: null,
    attemptFingerprint: null, passedAt: null, failedAt: null, trustLabel: 'USER REPORTED',
  }
}

function buildImplementationReviewTask(id: string): EngineeringTask {
  const completedAt = '2026-07-18T12:07:00.000Z'
  return buildTask(id, 'AwaitingImplementationReview', {
    planApprovedAt: completedAt,
    implementationPlan: buildPlan(),
    implementationWorkspace: {
      branch: `forge/task-${id.replaceAll('-', '')}`,
      baseCommitSha: '0123456789abcdef0123456789abcdef01234567',
      phase: 'Completed',
      createdAt: completedAt,
      updatedAt: completedAt,
      isAvailable: true,
    },
    implementationResult: {
      source: 'DeterministicFake',
      model: null,
      baseCommitSha: '0123456789abcdef0123456789abcdef01234567',
      branch: `forge/task-${id.replaceAll('-', '')}`,
      summary: 'Mechanical Fake changes are ready for human diff review.',
      warnings: ['This is a mechanical workflow demonstration, not AI-authored implementation.', 'Validation commands were not run.'],
      changedFiles: [{
        path: 'src/ReportExportService.cs',
        action: 'Modify',
        originalContentSha256: 'old',
        newContentSha256: 'new',
        originalBytes: 20,
        newBytes: 80,
        originalLines: 1,
        newLines: 2,
        additions: 1,
        deletions: 0,
        diffPreview: 'diff --git a/src/ReportExportService.cs b/src/ReportExportService.cs\n--- a/src/ReportExportService.cs\n+++ b/src/ReportExportService.cs\n-old\n+new',
        fullDiffCharacters: 120,
        displayedDiffCharacters: 70,
        diffTruncated: true,
        fullDiffUtf8Bytes: 120,
        displayedDiffUtf8Bytes: 70,
      }],
      fullDiffCharacters: 120,
      displayedDiffCharacters: 70,
      diffTruncated: true,
      completedAt,
      isDeterministicFake: true,
      fullDiffUtf8Bytes: 120,
      displayedDiffUtf8Bytes: 70,
      activeCheckoutVerified: true,
    },
    implementationRuntime: { workspaceAvailable: true, activeCheckoutVerified: true, disposition: 'Completed', safeMessage: null },
    implementationStartedAt: completedAt,
    implementationCompletedAt: completedAt,
    rowVersion: 7,
    activeImplementationRevisionId: '11111111-1111-4111-8111-111111111111',
    implementationRevisions: [{
      revisionId: '11111111-1111-4111-8111-111111111111',
      revisionNumber: 1,
      kind: 'Initial',
      previousRevisionId: null,
      planFingerprint: 'a'.repeat(64),
      baseCommitSha: '0123456789abcdef0123456789abcdef01234567',
      generationStartedAt: completedAt,
      generationCompletedAt: completedAt,
      generationState: 'Succeeded',
      reviewState: 'Current',
      failureCategory: null,
      failureMessage: null,
      resultFingerprint: 'b'.repeat(64),
      changedFileCount: 1,
      correctionSubmittedAt: null,
      approvedAt: null,
      isCurrent: true,
      isApproved: false,
    }],
  })
}

function buildSummary(task: EngineeringTask, preview: string): EngineeringTaskSummary {
  return {
    id: task.id,
    status: task.status,
    createdAt: task.createdAt,
    updatedAt: task.updatedAt,
    repository: task.repository,
    originalRequirementPreview: preview,
  }
}

function buildPlan(): ImplementationPlan {
  return {
    title: 'Portable task export plan',
    objective: 'Export task and plan documents.',
    repositoryUnderstanding: 'The repository already has PDF export flow and task history persistence.',
    affectedFiles: [
      {
        path: 'web/forge-web/src/App.tsx',
        action: 'Modify',
        purpose: 'Expose task navigation and download actions.',
        evidenceIds: ['E1'],
        confidence: 0.82,
      },
    ],
    orderedSteps: [
      {
        order: 1,
        description: 'Wire task navigation and PDF entry points into the task screen.',
        affectedPaths: ['web/forge-web/src/App.tsx'],
        evidenceIds: ['E1'],
        expectedResult: 'The screen exposes navigation and export controls without mutating persisted tasks.',
      },
    ],
    proposedValidationCommands: ['npm test -- --run'],
    risks: ['Download state could hide task navigation if not coordinated carefully.'],
    assumptions: ['Persisted task IDs remain the canonical navigation key.'],
    unresolvedQuestions: [],
    requirementCoverage: [
      {
        requirement: 'Provide portable task documentation and navigation.',
        affectedPaths: ['web/forge-web/src/App.tsx'],
        stepOrders: [1],
      },
    ],
    summary: 'The UI exposes navigation and export actions around persisted tasks.',
    source: 'DeterministicFake',
    planningModel: null,
    isDeterministicFake: true,
    createdAt: '2026-07-18T12:00:00.000Z',
    repositoryFingerprint: 'fingerprint',
  }
}
