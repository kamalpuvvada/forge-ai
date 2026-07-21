import { afterEach, describe, expect, it, vi } from 'vitest'
import { decodeEngineeringTask, decodeEngineeringTaskSummaries, decodeSystemCapabilities, ForgeApiError, forgeApi, parseSafePdfFilename } from './api'

function taskJson(overrides: Record<string, unknown> = {}) {
  return {
    id: '328cbf18-00ca-4fa8-a64e-3a651fb99079', status: 'ImplementationApproved', rowVersion: 1,
    repository: 'C:/safe-repository', originalRequirement: 'Make a safe change.',
    currentClarifiedRequirement: 'Make a safe change.', clarificationAnswers: [], requirementRevisionNotes: [],
    planRevisionNotes: [], currentPendingQuestion: null, requirementSummary: 'Make a safe change.',
    createdAt: '2026-07-20T11:00:00Z', updatedAt: '2026-07-20T12:00:00Z',
    requirementApprovedAt: '2026-07-20T11:10:00Z', planApprovedAt: '2026-07-20T11:20:00Z',
    repositorySnapshot: null, evidenceItems: [], evidenceFilesInspected: 0, evidenceFilesSelected: 0,
    totalEvidenceCharacters: 0, implementationPlan: null, repositoryAnalyzedAt: null,
    repositoryFingerprint: null, planCreatedAt: null, implementationWorkspace: null,
    implementationResult: null, lastImplementationFailure: null, implementationStartedAt: null,
    implementationCompletedAt: null, implementationRuntime: null, activeImplementationRevisionId: null,
    approvedImplementationRevisionId: null, telemetry: telemetryJson(),
    implementationRevisions: [],
    currentVerificationPlanId: null, currentVerificationAttemptId: null, verificationPlans: [],
    verificationPlanGenerationAttempts: [], manualVerificationAttempts: [],
    verificationEligibility: {
      canGenerateVerificationPlan: true, isInitialVerificationPlanGeneration: true,
      canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'NotStarted',
      verificationGenerationStatusMessage: 'Verification-plan generation is ready to start.',
      canStartVerificationAttempt: false, canRecordVerificationResult: false,
      canCompleteVerificationPassed: false, canCompleteVerificationFailed: false,
      readyForDelivery: false, ineligibilityReason: null,
    },
    correctionEligibility: {
      canGenerateFailureAnalysis: false, canApproveCorrection: false, canGenerateCorrection: false,
      canApproveCorrectedRevision: false, canGenerateReplacementVerificationPlan: false,
    },
    ...overrides,
  }
}

function telemetryJson(overrides: Record<string, unknown> = {}) {
  return {
    totalCalls: 0, usageAvailability: 'Complete', usageUnavailableCallCount: 0,
    totalInputTokens: 0, totalCachedInputTokens: 0, totalOutputTokens: 0, totalReasoningTokens: 0,
    totalEstimatedCostUsd: 0, costUnavailableCallCount: 0, isPartialEstimate: false,
    verificationLogicalAttemptCount: 0, verificationPhysicalRequestCount: 0,
    verificationPossiblyDispatchedRequestCount: 0, verificationDefinitelyUndispatchedAttemptCount: 0,
    completeEstimatedSubtotalUsd: null, partialEstimatedSubtotalUsd: null, availableEstimatedSubtotalUsd: 0,
    hasPartialEstimates: false, possiblyDispatchedUnavailableEstimatedCostCallCount: 0, calls: [],
    ...overrides,
  }
}

function planJson(overrides: Record<string, unknown> = {}) {
  return {
    planId: '11111111-1111-4111-8111-111111111111', planNumber: 1,
    implementationRevisionId: '22222222-2222-4222-8222-222222222222',
    implementationResultFingerprint: 'a'.repeat(64), approvedRequirementFingerprint: 'b'.repeat(64),
    approvedPlanFingerprint: 'c'.repeat(64), generationContextFingerprint: 'd'.repeat(64),
    generatedAt: '2026-07-20T12:00:00Z', source: 'DeterministicFake', model: null, reasoningEffort: null,
    summary: 'Manual checks.', scope: 'Approved revision.', preconditions: [], testCases: [], risks: [],
    limitations: [], evidenceGuidance: [], planFingerprint: 'e'.repeat(64), status: 'Current',
    trustLabel: 'FORGE GENERATED', executionLabel: 'MANUAL — NOT EXECUTED BY FORGE', ...overrides,
  }
}

function testCaseJson(overrides: Record<string, unknown> = {}) {
  return {
    testCaseId: '33333333-3333-4333-8333-333333333333', order: 1, title: 'Manual check',
    objective: 'Observe behavior.', category: 'ManualBehavior', isRequired: true, preconditions: [], testData: [],
    orderedSteps: [{ order: 1, instruction: 'Inspect behavior.', approvedValidationCommandId: null,
      expectedObservation: 'Behavior is correct.' }], expectedResult: 'Behavior is correct.',
    negativeOrEdgeCases: [], regressionScope: [], evidenceRequirements: [], safetyNotes: [], ...overrides,
  }
}

function resultJson(overrides: Record<string, unknown> = {}) {
  return {
    resultRevisionId: '44444444-4444-4444-8444-444444444444', revisionNumber: 1,
    testCaseId: '33333333-3333-4333-8333-333333333333', result: 'Passed',
    recordedAt: '2026-07-20T12:01:00Z', notes: null, actualResult: 'Observed.', evidenceDescriptions: [],
    notApplicableReason: null, failureDetails: null, supersedesResultRevisionId: null, trustLabel: 'USER REPORTED', ...overrides,
  }
}

function manualAttemptJson(overrides: Record<string, unknown> = {}) {
  return {
    attemptId: '55555555-5555-4555-8555-555555555555', attemptNumber: 1,
    verificationPlanId: '11111111-1111-4111-8111-111111111111', verificationPlanFingerprint: 'e'.repeat(64),
    implementationRevisionId: '22222222-2222-4222-8222-222222222222',
    implementationResultFingerprint: 'a'.repeat(64), startedAt: '2026-07-20T12:00:00Z', completedAt: null,
    status: 'InProgress', resultRevisions: [], currentCaseResults: [], completionConfirmation: null,
    summary: null, attemptFingerprint: null, passedAt: null, failedAt: null, trustLabel: 'USER REPORTED',
    ...overrides,
  }
}

function manualTaskJson(options: {
  status?: string
  attempt?: Record<string, unknown> | null
  planOverrides?: Record<string, unknown>
  eligibility?: Record<string, unknown>
  taskOverrides?: Record<string, unknown>
} = {}) {
  const status = options.status ?? 'AwaitingManualVerification'
  const testCase = testCaseJson()
  const plan = planJson({ testCases: [testCase], status: status === 'AwaitingManualVerification' ? 'Current' : 'Completed',
    ...options.planOverrides })
  const attempt = options.attempt === undefined ? null : options.attempt
  const eligibility = {
    canGenerateVerificationPlan: false, isInitialVerificationPlanGeneration: false,
    canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'Completed',
    verificationGenerationStatusMessage: 'Verification-plan generation completed.',
    canStartVerificationAttempt: attempt === null && status === 'AwaitingManualVerification',
    canRecordVerificationResult: false, canCompleteVerificationPassed: false,
    canCompleteVerificationFailed: false, readyForDelivery: status === 'ReadyForDelivery',
    ineligibilityReason: null, ...options.eligibility,
  }
  return taskJson({ status, activeImplementationRevisionId: '22222222-2222-4222-8222-222222222222',
    approvedImplementationRevisionId: '22222222-2222-4222-8222-222222222222',
    implementationRevisions: [implementationRevisionJson({ resultFingerprint: 'a'.repeat(64),
      reviewState: 'Approved', isApproved: true, approvedAt: '2026-07-20T11:50:00Z' })],
    currentVerificationPlanId: plan.planId, verificationPlans: [plan],
    currentVerificationAttemptId: attempt?.attemptId ?? null,
    manualVerificationAttempts: attempt === null ? [] : [attempt], verificationEligibility: eligibility,
    correctionEligibility: {
      canGenerateFailureAnalysis: status === 'ManualVerificationFailed', canApproveCorrection: false,
      canGenerateCorrection: false, canApproveCorrectedRevision: false,
      canGenerateReplacementVerificationPlan: false,
    },
    ...options.taskOverrides })
}

function completedAttempt(status: 'CompletedPassed' | 'CompletedFailed') {
  return manualAttemptJson({ status, completedAt: '2026-07-20T12:10:00Z', completionConfirmation: true,
    attemptFingerprint: 'f'.repeat(64), passedAt: status === 'CompletedPassed' ? '2026-07-20T12:10:00Z' : null,
    failedAt: status === 'CompletedFailed' ? '2026-07-20T12:10:00Z' : null })
}

function correctionTaskJson(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const failedAttempt = completedAttempt('CompletedFailed')
  const analysis = {
    analysisId: '66666666-6666-4666-8666-666666666666', analysisNumber: 1,
    generationCommandId: '77777777-7777-4777-8777-777777777777', contextFingerprint: '1'.repeat(64),
    failedAttemptId: failedAttempt.attemptId, failedAttemptFingerprint: failedAttempt.attemptFingerprint,
    verificationPlanId: '11111111-1111-4111-8111-111111111111', verificationPlanFingerprint: 'e'.repeat(64),
    implementationRevisionId: '22222222-2222-4222-8222-222222222222',
    implementationResultFingerprint: 'a'.repeat(64), classification: 'ImplementationDefect', confidencePercent: 50,
    rootCauseSummary: 'Bounded synthetic root cause.', rationale: 'Bounded synthetic rationale.',
    evidenceReferences: [failedAttempt.attemptId], affectedApprovedOperations: [{ path: 'src/App.cs', action: 'Modify' }],
    correctionStrategy: 'Modify the approved file only.', expectedBehavior: 'The manual case passes.',
    verificationImpact: 'Repeat the failed case.', risks: ['Human review remains required.'],
    source: 'DeterministicFake', model: null, reasoningEffort: null, modelCallIds: [],
    analysisFingerprint: '2'.repeat(64), status: 'Completed', createdAt: '2026-07-20T12:11:00Z',
    trustLabel: 'FORGE GENERATED', safeRoute: 'Correction proposal available after explicit approval.',
  }
  const proposal = {
    proposalId: '88888888-8888-4888-8888-888888888888', proposalNumber: 1,
    analysisId: analysis.analysisId, analysisFingerprint: analysis.analysisFingerprint,
    failedAttemptId: analysis.failedAttemptId, failedAttemptFingerprint: analysis.failedAttemptFingerprint,
    previousApprovedRevisionId: analysis.implementationRevisionId,
    previousResultFingerprint: analysis.implementationResultFingerprint,
    approvedRequirementFingerprint: '3'.repeat(64), approvedPlanFingerprint: '4'.repeat(64),
    originalBaseCommitSha: '5'.repeat(40), affectedApprovedOperations: analysis.affectedApprovedOperations,
    rootCauseSummary: analysis.rootCauseSummary, correctionStrategy: analysis.correctionStrategy,
    expectedBehavior: analysis.expectedBehavior, verificationImpact: analysis.verificationImpact,
    risks: analysis.risks, proposalFingerprint: '6'.repeat(64), status: 'AwaitingApproval',
    createdAt: '2026-07-20T12:12:00Z', approvedAt: null,
  }
  const generation = {
    commandId: analysis.generationCommandId, expectedFailedAttemptId: analysis.failedAttemptId,
    expectedFailedAttemptFingerprint: analysis.failedAttemptFingerprint, startedAt: '2026-07-20T12:10:00Z',
    leaseExpiresAt: '2026-07-20T12:15:00Z', updatedAt: '2026-07-20T12:11:00Z',
    completedAt: '2026-07-20T12:11:00Z', status: 'Completed', logicalCallCount: 0,
    physicalRequestCount: 0, possiblyDispatchedRequestCount: 0, definitelyUndispatchedRequestCount: 0,
    activeRequestCount: 0, modelCallIds: [], providerResponses: [], resultAnalysisId: analysis.analysisId,
    failureCategory: null, failureMessage: null, retryEligible: false, recoveryRequired: false,
  }
  return manualTaskJson({ status: 'AwaitingCorrectionApproval', attempt: failedAttempt,
    taskOverrides: {
      currentFailureAnalysisId: analysis.analysisId, currentCorrectionProposalId: proposal.proposalId,
      failureAnalyses: [analysis], correctionProposals: [proposal], failureAnalysisGenerationAttempts: [generation],
      correctionGenerationAttempts: [], correctionApprovalAudit: [],
      correctionEligibility: { canGenerateFailureAnalysis: false, canApproveCorrection: true,
        canGenerateCorrection: false, canApproveCorrectedRevision: false,
        canGenerateReplacementVerificationPlan: false },
      ...overrides,
    } })
}

function approvedRevisionTwoTaskJson(withPlanTwo = false) {
  const value = correctionTaskJson()
  const revision1 = (value.implementationRevisions as Array<Record<string, unknown>>)[0]
  const plan1 = (value.verificationPlans as Array<Record<string, unknown>>)[0]
  const proposal = (value.correctionProposals as Array<Record<string, unknown>>)[0]
  const revision2Id = '99999999-9999-4999-8999-999999999999'
  const revision2Fingerprint = '9'.repeat(64)
  const correctionCommandId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
  revision1.reviewState = 'HistoricallyApproved'
  revision1.isCurrent = false
  revision1.isApproved = false
  plan1.status = 'Superseded'
  proposal.status = 'Approved'
  proposal.approvedAt = '2026-07-20T12:13:00Z'
  const revision2 = implementationRevisionJson({
    revisionId: revision2Id, revisionNumber: 2, kind: 'Correction',
    previousRevisionId: revision1.revisionId, resultFingerprint: revision2Fingerprint,
    reviewState: 'Approved', isCurrent: true, isApproved: true,
    approvedAt: '2026-07-20T12:20:00Z', correctionSubmittedAt: '2026-07-20T12:13:00Z',
    correctionProposalId: proposal.proposalId, correctionProposalFingerprint: proposal.proposalFingerprint,
  })
  const correctionAttempt = {
    attemptId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', commandId: correctionCommandId,
    proposalId: proposal.proposalId, proposalFingerprint: proposal.proposalFingerprint,
    previousRevisionId: revision1.revisionId, previousResultFingerprint: revision1.resultFingerprint,
    startedAt: '2026-07-20T12:14:00Z', leaseExpiresAt: '2026-07-20T12:19:00Z',
    updatedAt: '2026-07-20T12:18:00Z', completedAt: '2026-07-20T12:18:00Z', status: 'Completed',
    logicalCallCount: 0, physicalRequestCount: 0, possiblyDispatchedRequestCount: 0,
    definitelyUndispatchedRequestCount: 0, activeRequestCount: 0, modelCallIds: [], providerResponses: [],
    acceptedOutputFingerprint: '8'.repeat(64), revisionId: revision2Id,
    failureCategory: null, failureMessage: null, retryEligible: false, recoveryRequired: false,
  }
  const approval = {
    commandId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', proposalId: proposal.proposalId,
    proposalFingerprint: proposal.proposalFingerprint, expectedRowVersion: 20, completedRowVersion: 21,
    createdAt: '2026-07-20T12:12:30Z', completedAt: '2026-07-20T12:13:00Z', result: 'Approved',
  }
  const plan2 = planJson({
    planId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', planNumber: 2,
    implementationRevisionId: revision2Id, implementationResultFingerprint: revision2Fingerprint,
    generatedAt: '2026-07-20T12:21:00Z', planFingerprint: '7'.repeat(64), status: 'Current',
  })
  return {
    ...value,
    status: withPlanTwo ? 'AwaitingManualVerification' : 'ImplementationApproved',
    activeImplementationRevisionId: revision2Id,
    approvedImplementationRevisionId: revision2Id,
    pendingImplementationRevisionId: null,
    implementationRevisions: [revision1, revision2],
    implementationResult: implementationResultJson({
      baseCommitSha: revision2.baseCommitSha, completedAt: revision2.generationCompletedAt,
    }),
    verificationPlans: withPlanTwo ? [plan1, plan2] : [plan1],
    currentVerificationPlanId: withPlanTwo ? plan2.planId : null,
    currentVerificationAttemptId: null,
    correctionGenerationAttempts: [correctionAttempt],
    correctionApprovalAudit: [approval],
    correctionEligibility: {
      canGenerateFailureAnalysis: false, canApproveCorrection: false, canGenerateCorrection: false,
      canApproveCorrectedRevision: false, canGenerateReplacementVerificationPlan: !withPlanTwo,
    },
    verificationEligibility: {
      canGenerateVerificationPlan: !withPlanTwo, isInitialVerificationPlanGeneration: false,
      canRetryVerificationPlanGeneration: false, verificationGenerationStatus: withPlanTwo ? 'Completed' : 'NotStarted',
      verificationGenerationStatusMessage: withPlanTwo ? 'Verification-plan generation completed.' : 'Replacement verification-plan generation is ready.',
      canStartVerificationAttempt: withPlanTwo, canRecordVerificationResult: false,
      canCompleteVerificationPassed: false, canCompleteVerificationFailed: false,
      readyForDelivery: false, ineligibilityReason: null,
    },
  }
}

function persistedCorrectionReviewTaskJson(): Record<string, unknown> {
  const value = approvedRevisionTwoTaskJson() as Record<string, unknown>
  const revision1 = (value.implementationRevisions as Array<Record<string, unknown>>)[0]
  const revision2 = (value.implementationRevisions as Array<Record<string, unknown>>)[1]
  const plan1 = (value.verificationPlans as Array<Record<string, unknown>>)[0]
  const attempt1 = (value.manualVerificationAttempts as Array<Record<string, unknown>>)[0]
  const correctionAttempt = (value.correctionGenerationAttempts as Array<Record<string, unknown>>)[0]
  const implementationCall = completeVerificationCall({
    id: 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee', stage: 'Implementation',
    providerUsageAvailability: null, providerUsageAvailable: true,
  })

  revision1.reviewState = 'Approved'
  revision1.isCurrent = false
  revision1.isApproved = true
  revision2.reviewState = 'Current'
  revision2.isCurrent = true
  revision2.isApproved = false
  revision2.approvedAt = null
  plan1.status = 'Completed'
  correctionAttempt.logicalCallCount = 1
  correctionAttempt.physicalRequestCount = 1
  correctionAttempt.modelCallIds = [implementationCall.id]

  return {
    ...value,
    status: 'AwaitingImplementationReview',
    activeImplementationRevisionId: revision2.revisionId,
    approvedImplementationRevisionId: revision1.revisionId,
    currentVerificationPlanId: plan1.planId,
    currentVerificationAttemptId: attempt1.attemptId,
    implementationResult: implementationResultJson({
      source: 'OpenAI', model: 'gpt-test', isDeterministicFake: false,
      baseCommitSha: revision2.baseCommitSha, completedAt: revision2.generationCompletedAt,
    }),
    implementationRuntime: {
      workspaceAvailable: true, activeCheckoutVerified: true, disposition: 'Completed',
      safeMessage: 'The previous implementation attempt was interrupted.',
    },
    telemetry: telemetryJson({
      totalCalls: 1, totalInputTokens: 12, totalCachedInputTokens: 2, totalOutputTokens: 7,
      totalReasoningTokens: 1, totalEstimatedCostUsd: 0.000244, completeEstimatedSubtotalUsd: 0.000244,
      availableEstimatedSubtotalUsd: 0.000244, calls: [implementationCall],
    }),
    correctionEligibility: {
      canGenerateFailureAnalysis: false, canApproveCorrection: false, canGenerateCorrection: false,
      canApproveCorrectedRevision: true, canGenerateReplacementVerificationPlan: false,
    },
    verificationEligibility: {
      canGenerateVerificationPlan: false, isInitialVerificationPlanGeneration: false,
      canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'Completed',
      verificationGenerationStatusMessage: 'Verification-plan generation completed.',
      canStartVerificationAttempt: false, canRecordVerificationResult: false,
      canCompleteVerificationPassed: false, canCompleteVerificationFailed: false,
      readyForDelivery: false, ineligibilityReason: 'The approved implementation is not ready for manual verification.',
    },
  }
}

function generationAttemptJson(overrides: Record<string, unknown> = {}) {
  return {
    commandId: '11111111-1111-4111-8111-111111111111', startedAt: '2026-07-20T12:00:00Z',
    leaseExpiresAt: '2026-07-20T12:05:00Z', completedAt: null, status: 'ResponseReceived',
    failureCategory: null, failureMessage: null, resultPlanId: null,
    lastLogicalCallId: '66666666-6666-4666-8666-666666666666', logicalCallCount: 1,
    physicalRequestCount: 1, possiblyDispatchedRequestCount: 0,
    modelCallIds: ['66666666-6666-4666-8666-666666666666'],
    logicalCalls: [{ logicalCallId: '66666666-6666-4666-8666-666666666666', startedAt: '2026-07-20T12:00:00Z' }],
    providerResponses: [], ...overrides,
  }
}

function implementationRevisionJson(overrides: Record<string, unknown> = {}) {
  return {
    revisionId: '22222222-2222-4222-8222-222222222222', revisionNumber: 1, kind: 'Initial',
    previousRevisionId: null, planFingerprint: 'a'.repeat(64), baseCommitSha: 'b'.repeat(40),
    generationStartedAt: '2026-07-20T11:30:00Z', generationCompletedAt: '2026-07-20T11:40:00Z',
    generationState: 'Succeeded', reviewState: 'Current', failureCategory: null, failureMessage: null,
    resultFingerprint: 'c'.repeat(64), changedFileCount: 1, correctionSubmittedAt: null, approvedAt: null,
    isCurrent: true, isApproved: false, ...overrides,
  }
}

function implementationResultJson(overrides: Record<string, unknown> = {}) {
  const diff = 'diff --git a/src/App.cs b/src/App.cs'
  return {
    source: 'DeterministicFake', model: null, baseCommitSha: 'b'.repeat(40), branch: 'forge/generated-change',
    summary: 'Review the bounded change.', warnings: [], changedFiles: [{ path: 'src/App.cs', action: 'Modify',
      originalContentSha256: 'd'.repeat(64), newContentSha256: 'e'.repeat(64), originalBytes: 10, newBytes: 20,
      originalLines: 1, newLines: 2, additions: 1, deletions: 0, diffPreview: diff,
      fullDiffCharacters: diff.length, displayedDiffCharacters: diff.length, diffTruncated: false,
      fullDiffUtf8Bytes: diff.length, displayedDiffUtf8Bytes: diff.length }],
    fullDiffCharacters: diff.length, displayedDiffCharacters: diff.length, diffTruncated: false,
    completedAt: '2026-07-20T11:40:00Z', isDeterministicFake: true,
    fullDiffUtf8Bytes: diff.length, displayedDiffUtf8Bytes: diff.length, activeCheckoutVerified: true,
    ...overrides,
  }
}

function completeVerificationCall(overrides: Record<string, unknown> = {}) {
  return {
    id: '66666666-6666-4666-8666-666666666666', stage: 'VerificationPlanning', provider: 'OpenAI',
    model: 'gpt-test', reasoningEffort: 'medium', startedAt: '2026-07-20T12:00:00Z',
    completedAt: '2026-07-20T12:00:01Z', succeeded: true, providerResponseId: 'resp_safe',
    usageAvailable: true, inputTokens: 12, cachedInputTokens: 2, uncachedInputTokens: 10,
    outputTokens: 7, reasoningTokens: 1, estimatedCostUsd: 0.000244,
    pricingProvenance: 'stored pricing snapshot', hasStoredPricingSnapshot: true,
    storedPricingSnapshot: { inputPerMillionUsd: 10, cachedInputPerMillionUsd: 2, outputPerMillionUsd: 20 },
    failureCategory: null, providerRequestId: 'req_safe', providerUsageAvailability: 'Complete',
    providerUsageAvailable: true, verificationDispatchDisposition: 'ResponseReceived', providerHttpStatusCode: 200,
    isPartialEstimate: false, ...overrides,
  }
}

function completeTelemetry(overrides: Record<string, unknown> = {}) {
  return telemetryJson({ totalCalls: 1, totalInputTokens: 12, totalCachedInputTokens: 2,
    totalOutputTokens: 7, totalReasoningTokens: 1, totalEstimatedCostUsd: 0.000244,
    verificationLogicalAttemptCount: 1, verificationPhysicalRequestCount: 1,
    completeEstimatedSubtotalUsd: 0.000244, availableEstimatedSubtotalUsd: 0.000244,
    calls: [completeVerificationCall()], ...overrides })
}

describe('task PDF API helper', () => {
  it('decodes the persisted successful correction revision-two response shape', () => {
    const decoded = decodeEngineeringTask(persistedCorrectionReviewTaskJson())

    expect(decoded.status).toBe('AwaitingImplementationReview')
    expect(decoded.activeImplementationRevisionId).toBe(decoded.implementationRevisions[1].revisionId)
    expect(decoded.approvedImplementationRevisionId).toBe(decoded.implementationRevisions[0].revisionId)
    expect(decoded.correctionEligibility?.canApproveCorrectedRevision).toBe(true)
  })

  it('decodes the same persisted correction response again through the refresh GET', async () => {
    const persisted = persistedCorrectionReviewTaskJson()
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(persisted), {
      status: 200, headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetch)

    expect((await forgeApi.getTask(persisted.id as string)).status).toBe('AwaitingImplementationReview')
    expect(fetch).toHaveBeenCalledWith(`/api/tasks/${persisted.id}`, expect.anything())
  })

  it('accepts historical completed plan one and failed attempt one during correction review', () => {
    const decoded = decodeEngineeringTask(persistedCorrectionReviewTaskJson())

    expect(decoded.verificationPlans?.[0].status).toBe('Completed')
    expect(decoded.manualVerificationAttempts?.[0].status).toBe('CompletedFailed')
    expect(decoded.verificationPlans?.[0].implementationRevisionId).toBe(decoded.implementationRevisions[0].revisionId)
  })

  it.each([
    ['contradictory provider usage flag', { providerUsageAvailable: false }],
    ['non-response dispatch with reported provider usage', { verificationDispatchDisposition: 'PossiblyDispatched' }],
  ])('rejects implementation telemetry with %s', (_, callOverrides) => {
    const value = persistedCorrectionReviewTaskJson()
    Object.assign((value.telemetry as { calls: Array<Record<string, unknown>> }).calls[0], callOverrides)

    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it.each([
    ['plan one revision', (value: Record<string, unknown>) => {
      const revisions = value.implementationRevisions as Array<Record<string, unknown>>
      ;(value.verificationPlans as Array<Record<string, unknown>>)[0].implementationRevisionId = revisions[1].revisionId
    }],
    ['attempt one plan', (value: Record<string, unknown>) => {
      ;(value.manualVerificationAttempts as Array<Record<string, unknown>>)[0].verificationPlanId =
        'ffffffff-ffff-4fff-8fff-ffffffffffff'
    }],
    ['attempt one revision', (value: Record<string, unknown>) => {
      const revisions = value.implementationRevisions as Array<Record<string, unknown>>
      ;(value.manualVerificationAttempts as Array<Record<string, unknown>>)[0].implementationRevisionId = revisions[1].revisionId
    }],
  ])('rejects a persisted correction review with a mismatched %s binding', (_, mutate) => {
    const value = persistedCorrectionReviewTaskJson()
    mutate(value)

    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('rejects reversed active and approved correction-review pointers', () => {
    const value = persistedCorrectionReviewTaskJson()
    const active = value.activeImplementationRevisionId
    value.activeImplementationRevisionId = value.approvedImplementationRevisionId
    value.approvedImplementationRevisionId = active

    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('rejects a correction candidate already marked approved before explicit approval', () => {
    const value = persistedCorrectionReviewTaskJson()
    const candidate = (value.implementationRevisions as Array<Record<string, unknown>>)[1]
    candidate.reviewState = 'Approved'
    candidate.isApproved = true
    candidate.approvedAt = '2026-07-20T12:20:00Z'

    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it.each([
    ['premature historical state on revision one', (value: Record<string, unknown>) => {
      ;(value.implementationRevisions as Array<Record<string, unknown>>)[0].reviewState = 'HistoricallyApproved'
    }],
    ['missing current correction proposal', (value: Record<string, unknown>) => {
      value.currentCorrectionProposalId = null
    }],
    ['mismatched correction proposal fingerprint', (value: Record<string, unknown>) => {
      ;(value.implementationRevisions as Array<Record<string, unknown>>)[1].correctionProposalFingerprint = 'f'.repeat(64)
    }],
  ])('rejects correction review with %s', (_, mutate) => {
    const value = persistedCorrectionReviewTaskJson()
    mutate(value)

    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('rejects forged corrected-revision approval eligibility', () => {
    const value = persistedCorrectionReviewTaskJson()
    ;(value.correctionEligibility as Record<string, unknown>).canApproveCorrectedRevision = false

    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('rejects a top-level implementation result that does not match revision two', () => {
    const value = persistedCorrectionReviewTaskJson()
    ;(value.implementationResult as Record<string, unknown>).baseCommitSha = 'f'.repeat(40)

    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('rejects a third implementation revision in the bounded correction workflow', () => {
    const value = persistedCorrectionReviewTaskJson()
    const revisions = value.implementationRevisions as Array<Record<string, unknown>>
    revisions.push(implementationRevisionJson({
      revisionId: 'ffffffff-ffff-4fff-8fff-ffffffffffff', revisionNumber: 3, kind: 'Correction',
      previousRevisionId: revisions[1].revisionId, isCurrent: false, isApproved: false,
    }))

    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('accepts a correction response only when its authoritative relationships agree', () => {
    expect(decodeEngineeringTask(correctionTaskJson()).status).toBe('AwaitingCorrectionApproval')
  })

  it('accepts preserved historical correction evidence after revision-two approval and on refresh', () => {
    const value = approvedRevisionTwoTaskJson()
    expect(decodeEngineeringTask(value).status).toBe('ImplementationApproved')
    expect(decodeEngineeringTask(structuredClone(value)).currentVerificationPlanId).toBeNull()
  })

  it('accepts a current replacement plan bound to approved revision two', () => {
    const decoded = decodeEngineeringTask(approvedRevisionTwoTaskJson(true))
    expect(decoded.currentVerificationPlanId).toBe('dddddddd-dddd-4ddd-8ddd-dddddddddddd')
  })

  it.each([
    ['failed attempt', (value: Record<string, unknown>) => {
      ;(value.failureAnalyses as Array<Record<string, unknown>>)[0].failedAttemptId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'
    }],
    ['superseded plan', (value: Record<string, unknown>) => {
      ;(value.failureAnalyses as Array<Record<string, unknown>>)[0].verificationPlanId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee'
    }],
    ['current plan pointer', (value: Record<string, unknown>) => { value.currentVerificationPlanId = '11111111-1111-4111-8111-111111111111' }],
    ['current attempt pointer', (value: Record<string, unknown>) => { value.currentVerificationAttemptId = '55555555-5555-4555-8555-555555555555' }],
  ])('rejects a forged revision-two historical %s binding', (_, mutate) => {
    const value = approvedRevisionTwoTaskJson()
    mutate(value)
    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('rejects a validly shaped task returned for another requested task', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(approvedRevisionTwoTaskJson()), {
      status: 200, headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetch)
    await expect(forgeApi.getTask('b2ec060f-d030-42f0-bffb-bf3e5402ddb2'))
      .rejects.toThrow('The task response could not be validated safely.')
  })

  it('rejects forged correction eligibility', () => {
    const value = correctionTaskJson({ correctionEligibility: {
      canGenerateFailureAnalysis: false, canApproveCorrection: true, canGenerateCorrection: true,
      canApproveCorrectedRevision: false, canGenerateReplacementVerificationPlan: false,
    } })
    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('rejects a proposal bound to a different analysis', () => {
    const value = correctionTaskJson()
    const proposal = (value.correctionProposals as Array<Record<string, unknown>>)[0]
    proposal.analysisId = '99999999-9999-4999-8999-999999999999'
    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('rejects an unknown failure-analysis attempt status', () => {
    const value = correctionTaskJson()
    ;(value.failureAnalysisGenerationAttempts as Array<Record<string, unknown>>)[0].status = 'StillGeneratingForever'
    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  afterEach(() => vi.unstubAllGlobals())

  it('accepts internally consistent delivery capabilities and rejects legacy contradictions', () => {
    const value = {
      aiMode: 'Fake', clarificationProvider: 'Fake', clarificationModel: 'demo', reasoningEffort: 'low',
      clarificationConfigured: true, planningProvider: 'Fake', planningModel: 'demo',
      planningReasoningEffort: 'medium', planningConfigured: true, implementationProvider: 'Deterministic Fake',
      implementationModel: null, implementationReasoningEffort: null, implementationConfigured: true,
      aiConfigured: true, repositoryInspectionAvailable: true, planningAvailable: true,
      targetModificationAvailable: true, implementationApprovalAvailable: true,
      implementationCorrectionAvailable: true, validationAvailable: false, reviewAvailable: true,
      pullRequestCreationAvailable: true, fakeImplementationAvailable: true, openAiImplementationAvailable: false,
      silentFallbackSupported: false, commitAvailable: true, pushAvailable: true,
      deliveryPullRequestAvailable: true, verificationPlanningProvider: 'Fake', verificationPlanningModel: 'demo',
      verificationPlanningReasoningEffort: 'medium', verificationPlanningConfigured: true,
      manualResultRecordingAvailable: true, automatedValidationAvailable: false, failureAnalysisAvailable: true,
      failureAnalysisConfigured: true, implementationCorrectionConfigured: true,
      deliveryPullRequestConfigured: true, autoMergeAvailable: false,
      deliveryGitAvailable: true, deliveryGitHubCliAvailable: true,
    }

    expect(decodeSystemCapabilities(value).deliveryPullRequestConfigured).toBe(true)
    expect(() => decodeSystemCapabilities({ ...value, commitAvailable: false })).toThrow(/validated safely/)
    expect(() => decodeSystemCapabilities({ ...value, automatedValidationAvailable: true })).toThrow(/validated safely/)
    expect(() => decodeSystemCapabilities({ ...value, autoMergeAvailable: true })).toThrow(/validated safely/)
    expect(() => decodeSystemCapabilities({ ...value, deliveryGitHubCliAvailable: false })).toThrow(/validated safely/)
  })

  it('requests the PDF endpoint and returns its blob and safe filename', async () => {
    const blob = new Blob(['%PDF-1.7'], { type: 'application/pdf' })
    const fetch = vi.fn().mockResolvedValue(new Response(blob, {
      status: 200,
      headers: { 'Content-Disposition': 'attachment; filename="forge-task-abc.pdf"' },
    }))
    vi.stubGlobal('fetch', fetch)

    const result = await forgeApi.exportTaskPdf('abc')

    expect(fetch).toHaveBeenCalledWith('/api/tasks/abc/export/pdf', { headers: { Accept: 'application/pdf' } })
    expect(await result.blob.text()).toBe('%PDF-1.7')
    expect(result.filename).toBe('forge-task-abc.pdf')
  })

  it('rejects unsafe server filenames and uses a deterministic fallback', () => {
    expect(parseSafePdfFilename('attachment; filename="../../bad\r\nname.pdf"', 'safe-id'))
      .toBe('forge-task-safe-id.pdf')
    expect(parseSafePdfFilename("attachment; filename*=UTF-8''forge-task-safe-id.pdf", 'safe-id'))
      .toBe('forge-task-safe-id.pdf')
  })

  it('uses the existing safe problem response behavior', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      detail: 'The server could not generate this PDF.', code: 'server_error',
    }), { status: 500, headers: { 'Content-Type': 'application/problem+json' } })))

    await expect(forgeApi.exportTaskPdf('abc')).rejects.toMatchObject({
      message: 'The server could not generate this PDF.', code: 'server_error',
    } satisfies Partial<ForgeApiError>)
  })

  it('lists task summaries and downloads plan PDFs from distinct routes', async () => {
    const fetch = vi.fn()
      .mockResolvedValueOnce(new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(new Blob(['%PDF']), { status: 200,
        headers: { 'Content-Disposition': 'attachment; filename="forge-plan-abc.pdf"' } }))
    vi.stubGlobal('fetch', fetch)

    await expect(forgeApi.listTasks()).resolves.toEqual([])
    await expect(forgeApi.exportPlanPdf('abc')).resolves.toMatchObject({ filename: 'forge-plan-abc.pdf' })
    expect(fetch).toHaveBeenNthCalledWith(2, '/api/tasks/abc/export/plan-pdf', { headers: { Accept: 'application/pdf' } })
    expect(parseSafePdfFilename('attachment; filename="../bad.pdf"', 'abc', 'plan')).toBe('forge-plan-abc.pdf')
  })

  it('posts exact implementation approval preconditions as JSON', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(JSON.stringify(taskJson()), {
      status: 200, headers: { 'Content-Type': 'application/json' },
    }))
    vi.stubGlobal('fetch', fetch)
    const payload = {
      commandId: '11111111-1111-4111-8111-111111111111',
      expectedRowVersion: 7,
      expectedRevisionId: '22222222-2222-4222-8222-222222222222',
      expectedResultFingerprint: 'a'.repeat(64),
    }

    await forgeApi.approveImplementation('task-id', payload)

    expect(fetch).toHaveBeenCalledWith('/api/tasks/task-id/implementation-approval', {
      method: 'POST',
      body: JSON.stringify(payload),
      headers: { 'Content-Type': 'application/json' },
    })
  })

  it('preserves the serialized verification eligibility and partial-usage API shape', async () => {
    const fixture = taskJson({ status: 'VerificationPlanning', rowVersion: 4,
      telemetry: telemetryJson({ verificationLogicalAttemptCount: 1, verificationPhysicalRequestCount: 1 }),
      verificationEligibility: {
        canGenerateVerificationPlan: false, isInitialVerificationPlanGeneration: false,
        canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'AmbiguousAfterDispatch',
        verificationGenerationStatusMessage: 'Retry is disabled to avoid a duplicate billable request.',
        canStartVerificationAttempt: false, canRecordVerificationResult: false,
        canCompleteVerificationPassed: false, canCompleteVerificationFailed: false,
        readyForDelivery: false, ineligibilityReason: 'Generation outcome is ambiguous.',
      },
      verificationPlanGenerationAttempts: [generationAttemptJson({
        modelCallIds: [], lastLogicalCallId: '22222222-2222-4222-8222-222222222222',
        logicalCalls: [{ logicalCallId: '22222222-2222-4222-8222-222222222222', startedAt: '2026-07-20T12:00:01Z' }],
        providerResponses: [{ logicalCallId: '22222222-2222-4222-8222-222222222222', startedAt: '2026-07-20T12:00:01Z',
          receivedAt: '2026-07-20T12:00:02Z', usageAvailability: 'Partial', inputTokens: 12,
          cachedInputTokens: null, outputTokens: 7, reasoningTokens: null, status: 'Completed',
          providerResponseId: 'resp_safe', providerRequestId: null, incompleteReason: null,
          httpStatusCode: 200, dispatchDisposition: 'ResponseReceived' }],
      })],
    })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(fixture), {
      status: 200, headers: { 'Content-Type': 'application/json' },
    })))

    const result = await forgeApi.getTask('task-id')

    expect(result.verificationEligibility).toMatchObject(fixture.verificationEligibility)
    expect(result.verificationPlanGenerationAttempts?.[0].providerResponses[0]).toMatchObject({
      usageAvailability: 'Partial', inputTokens: 12, outputTokens: 7,
      startedAt: '2026-07-20T12:00:01Z', receivedAt: '2026-07-20T12:00:02Z',
    })
  })

  it.each([
    ['missing eligibility', undefined],
    ['malformed boolean', { canGenerateVerificationPlan: 'yes', isInitialVerificationPlanGeneration: false,
      canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'Active', verificationGenerationStatusMessage: null }],
    ['unknown status', { canGenerateVerificationPlan: false, isInitialVerificationPlanGeneration: false,
      canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'FutureState', verificationGenerationStatusMessage: null }],
    ['contradictory flags', { canGenerateVerificationPlan: false, isInitialVerificationPlanGeneration: false,
      canRetryVerificationPlanGeneration: true, verificationGenerationStatus: 'FailedBeforeDispatch', verificationGenerationStatusMessage: null }],
    ['ambiguous permission', { canGenerateVerificationPlan: true, isInitialVerificationPlanGeneration: false,
      canRetryVerificationPlanGeneration: false, verificationGenerationStatus: 'AmbiguousAfterDispatch', verificationGenerationStatusMessage: null }],
  ])('fails closed for %s', (_label, eligibility) => {
    expect(() => decodeEngineeringTask(taskJson({ status: 'VerificationPlanning', verificationEligibility: eligibility })))
      .toThrowError('The task response could not be validated safely.')
  })

  it.each([
    ['initial', 'NotStarted', true, true, false],
    ['safe retry', 'FailedBeforeDispatch', true, false, true],
    ['rejected provider output', 'RejectedProviderOutput', true, false, true],
    ['active', 'Active', false, false, false],
    ['ambiguous', 'AmbiguousAfterDispatch', false, false, false],
    ['completed', 'Completed', false, false, false],
  ])('accepts a representative %s eligibility response', (_label, status, canGenerate, initial, canRetry) => {
    const decoded = decodeEngineeringTask(taskJson({ status: status === 'NotStarted' ? 'ImplementationApproved' : 'VerificationPlanning',
      verificationEligibility: {
        canGenerateVerificationPlan: canGenerate, canStartVerificationAttempt: false,
        canRecordVerificationResult: false, canCompleteVerificationPassed: false,
        canCompleteVerificationFailed: false, readyForDelivery: false, ineligibilityReason: null,
        isInitialVerificationPlanGeneration: initial, canRetryVerificationPlanGeneration: canRetry,
        verificationGenerationStatus: status, verificationGenerationStatusMessage: 'Safe bounded status.',
      } }))

    expect(decoded.verificationEligibility?.verificationGenerationStatus).toBe(status)
    expect(decoded.verificationEligibility?.canGenerateVerificationPlan).toBe(canGenerate)
  })

  it('rejects malformed critical verification collections without exposing payload data', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify(taskJson({
      verificationPlanGenerationAttempts: [{ modelCallIds: [], logicalCalls: [], providerResponses: null }],
      secret: 'must-not-appear',
    })), { status: 200, headers: { 'Content-Type': 'application/json' } })))

    await expect(forgeApi.getTask('task-id')).rejects.toMatchObject({
      message: 'The task response could not be validated safely.',
    })
  })

  it.each([
    ['missing telemetry', undefined],
    ['unknown usage', telemetryJson({ usageAvailability: 'FutureUsage' })],
    ['negative token', telemetryJson({ totalInputTokens: -1 })],
    ['floating token', telemetryJson({ totalOutputTokens: 1.5 })],
    ['missing cost', telemetryJson({ totalEstimatedCostUsd: undefined })],
    ['malformed cost', telemetryJson({ totalEstimatedCostUsd: '0.01' })],
  ])('rejects %s instead of normalizing it to zero telemetry', (_label, telemetry) => {
    expect(() => decodeEngineeringTask(taskJson({ telemetry })))
      .toThrowError('The task response could not be validated safely.')
  })

  it('preserves a valid conservative partial telemetry shape', () => {
    const decoded = decodeEngineeringTask(taskJson({ verificationPlanGenerationAttempts: [generationAttemptJson()], telemetry: telemetryJson({
      totalCalls: 1, usageAvailability: 'Partial', usageUnavailableCallCount: 0,
      totalInputTokens: null, totalCachedInputTokens: null, totalOutputTokens: null, totalReasoningTokens: null,
      totalEstimatedCostUsd: null, costUnavailableCallCount: 0, isPartialEstimate: true,
      completeEstimatedSubtotalUsd: null, partialEstimatedSubtotalUsd: 0.00026,
      availableEstimatedSubtotalUsd: 0.00026, hasPartialEstimates: true,
      verificationLogicalAttemptCount: 1, verificationPhysicalRequestCount: 1,
      calls: [{
        id: '66666666-6666-4666-8666-666666666666', stage: 'VerificationPlanning', provider: 'OpenAI',
        model: 'gpt-test', reasoningEffort: 'medium', startedAt: '2026-07-20T12:00:00Z',
        completedAt: '2026-07-20T12:00:01Z', succeeded: true, providerResponseId: 'resp_safe',
        usageAvailable: true, inputTokens: 12, cachedInputTokens: null, uncachedInputTokens: 12,
        outputTokens: 7, reasoningTokens: null, estimatedCostUsd: 0.00026,
        pricingProvenance: 'stored pricing snapshot', hasStoredPricingSnapshot: true,
        storedPricingSnapshot: { inputPerMillionUsd: 10, cachedInputPerMillionUsd: 2, outputPerMillionUsd: 20 },
        failureCategory: null, providerRequestId: 'req_safe',
        providerUsageAvailability: 'Partial', providerUsageAvailable: true,
        verificationDispatchDisposition: 'ResponseReceived', providerHttpStatusCode: 200, isPartialEstimate: true,
      }],
    }) }))

    expect(decoded.telemetry.usageAvailability).toBe('Partial')
    expect(decoded.telemetry.partialEstimatedSubtotalUsd).toBe(0.00026)
    expect(decoded.telemetry.availableEstimatedSubtotalUsd).toBe(0.00026)
    expect(decoded.telemetry.calls[0].isPartialEstimate).toBe(true)
  })

  it('accepts representative complete and unavailable usage without inventing zero cost', () => {
    const complete = decodeEngineeringTask(taskJson({ verificationPlanGenerationAttempts: [generationAttemptJson()],
      telemetry: completeTelemetry() }))
    const unavailableCall = completeVerificationCall({ usageAvailable: false, providerUsageAvailable: false,
      providerUsageAvailability: 'Unavailable', inputTokens: null, cachedInputTokens: null,
      uncachedInputTokens: null, outputTokens: null, reasoningTokens: null, estimatedCostUsd: null,
      pricingProvenance: 'cost unavailable', isPartialEstimate: false })
    const unavailable = decodeEngineeringTask(taskJson({
      verificationPlanGenerationAttempts: [generationAttemptJson()],
      telemetry: telemetryJson({ totalCalls: 1, usageAvailability: 'Unavailable', usageUnavailableCallCount: 1,
        totalInputTokens: null, totalCachedInputTokens: null, totalOutputTokens: null, totalReasoningTokens: null,
        totalEstimatedCostUsd: null, costUnavailableCallCount: 1, isPartialEstimate: true,
        verificationLogicalAttemptCount: 1, verificationPhysicalRequestCount: 1,
        completeEstimatedSubtotalUsd: null, partialEstimatedSubtotalUsd: null,
        availableEstimatedSubtotalUsd: null, calls: [unavailableCall] }) }))

    expect(complete.telemetry.usageAvailability).toBe('Complete')
    expect(complete.telemetry.totalEstimatedCostUsd).toBe(0.000244)
    expect(unavailable.telemetry.usageAvailability).toBe('Unavailable')
    expect(unavailable.telemetry.totalEstimatedCostUsd).toBeNull()
  })

  it.each([
    ['null plan', { verificationPlans: [null] }],
    ['null test case', { verificationPlans: [planJson({ testCases: [null] })] }],
    ['null ordered step', { verificationPlans: [planJson({ testCases: [testCaseJson({ orderedSteps: [null] })] })] }],
    ['null generation attempt', { verificationPlanGenerationAttempts: [null] }],
    ['null manual attempt', { manualVerificationAttempts: [null] }],
    ['null result revision', { manualVerificationAttempts: [manualAttemptJson({ resultRevisions: [null] })] }],
    ['missing result test case', { manualVerificationAttempts: [manualAttemptJson({ resultRevisions: [resultJson({ testCaseId: undefined })] })] }],
    ['unknown result', { manualVerificationAttempts: [manualAttemptJson({ currentCaseResults: [resultJson({ result: 'FutureResult' })] })] }],
    ['malformed failure details', { manualVerificationAttempts: [manualAttemptJson({ currentCaseResults: [resultJson({ failureDetails: { severity: 'High' } })] })] }],
    ['null implementation revision', { implementationRevisions: [null] }],
  ])('rejects unsafe nested task data: %s', (_label, overrides) => {
    expect(() => decodeEngineeringTask(taskJson(overrides))).toThrowError(
      'The task response could not be validated safely.')
  })

  it.each([
    ['malformed clarification collection', { clarificationAnswers: [null] }],
    ['malformed requirement revision', { requirementRevisionNotes: [{ correction: 'x' }] }],
    ['malformed repository snapshot', { repositorySnapshot: { files: null } }],
    ['malformed evidence element', { evidenceItems: [null] }],
    ['malformed implementation revision', { implementationRevisions: [{ revisionId: 'unsafe' }] }],
    ['malformed implementation review', { implementationResult: implementationResultJson({ changedFiles: [null] }) }],
    ['duplicate implementation revision ID', { activeImplementationRevisionId: '22222222-2222-4222-8222-222222222222',
      implementationRevisions: [implementationRevisionJson(), implementationRevisionJson({ revisionNumber: 2,
        previousRevisionId: '22222222-2222-4222-8222-222222222222' })] }],
    ['unresolved current implementation revision', { activeImplementationRevisionId: '99999999-9999-4999-8999-999999999999',
      implementationRevisions: [implementationRevisionJson()] }],
  ])('rejects complete-task structure corruption: %s', (_label, overrides) => {
    expect(() => decodeEngineeringTask(taskJson(overrides)))
      .toThrowError('The task response could not be validated safely.')
  })

  it.each([
    ['malformed model call', completeVerificationCall({ id: 'bad' })],
    ['unknown usage enum', completeVerificationCall({ providerUsageAvailability: 'FutureUsage' })],
    ['UsageAvailable contradiction', completeVerificationCall({ usageAvailable: false })],
    ['Complete missing counters', completeVerificationCall({ reasoningTokens: null })],
    ['Partial with no counters', completeVerificationCall({ providerUsageAvailability: 'Partial', inputTokens: null,
      cachedInputTokens: null, uncachedInputTokens: null, outputTokens: null, reasoningTokens: null,
      estimatedCostUsd: null, isPartialEstimate: false })],
    ['cached input greater than input', completeVerificationCall({ cachedInputTokens: 13, uncachedInputTokens: 0 })],
    ['reasoning greater than output', completeVerificationCall({ reasoningTokens: 8 })],
    ['negative token', completeVerificationCall({ inputTokens: -1 })],
    ['fractional token', completeVerificationCall({ outputTokens: 1.5 })],
    ['invalid HTTP status', completeVerificationCall({ providerHttpStatusCode: 99 })],
    ['unavailable usage with zero cost', completeVerificationCall({ providerUsageAvailability: 'Unavailable',
      usageAvailable: false, providerUsageAvailable: false, inputTokens: null, cachedInputTokens: null,
      uncachedInputTokens: null, outputTokens: null, reasoningTokens: null, estimatedCostUsd: 0 })],
  ])('rejects model-call semantic corruption: %s', (_label, call) => {
    expect(() => decodeEngineeringTask(taskJson({ verificationPlanGenerationAttempts: [generationAttemptJson()],
      telemetry: completeTelemetry({ calls: [call] }) })))
      .toThrowError('The task response could not be validated safely.')
  })

  it.each([
    ['inconsistent complete subtotal', completeTelemetry({ completeEstimatedSubtotalUsd: 0.1 })],
    ['inconsistent partial subtotal', completeTelemetry({ partialEstimatedSubtotalUsd: 0.1 })],
    ['inconsistent available subtotal', completeTelemetry({ availableEstimatedSubtotalUsd: 0.1 })],
  ])('rejects telemetry accounting corruption: %s', (_label, telemetry) => {
    expect(() => decodeEngineeringTask(taskJson({ verificationPlanGenerationAttempts: [generationAttemptJson()], telemetry })))
      .toThrowError('The task response could not be validated safely.')
  })

  it.each([
    ['duplicate plan order', { verificationPlans: [planJson(), planJson({
      planId: '77777777-7777-4777-8777-777777777777' })] }],
    ['duplicate test-case order', { verificationPlans: [planJson({ testCases: [testCaseJson(), testCaseJson({
      testCaseId: '88888888-8888-4888-8888-888888888888' })] })] }],
    ['unresolved current plan', { currentVerificationPlanId: '99999999-9999-4999-8999-999999999999',
      verificationPlans: [planJson()] }],
  ])('rejects verification-plan relationship corruption: %s', (_label, verificationOverrides) => {
    expect(() => decodeEngineeringTask(taskJson({ activeImplementationRevisionId: '22222222-2222-4222-8222-222222222222',
      approvedImplementationRevisionId: '22222222-2222-4222-8222-222222222222',
      implementationRevisions: [implementationRevisionJson({ reviewState: 'Approved', isApproved: true })],
      ...verificationOverrides })))
      .toThrowError('The task response could not be validated safely.')
  })

  it.each([
    ['duplicate current result', [resultJson(), resultJson({ resultRevisionId: '99999999-9999-4999-8999-999999999999' })]],
    ['unknown result enum', [resultJson({ result: 'FutureResult' })]],
    ['result for unknown test case', [resultJson({ testCaseId: '99999999-9999-4999-8999-999999999999' })]],
    ['malformed failure details', [resultJson({ failureDetails: { severity: 'High' } })]],
  ])('rejects manual-result relationship corruption: %s', (_label, currentCaseResults) => {
    const revisions = currentCaseResults
    expect(() => decodeEngineeringTask(taskJson({
      activeImplementationRevisionId: '22222222-2222-4222-8222-222222222222',
      approvedImplementationRevisionId: '22222222-2222-4222-8222-222222222222',
      implementationRevisions: [implementationRevisionJson({ reviewState: 'Approved', isApproved: true })],
      currentVerificationPlanId: '11111111-1111-4111-8111-111111111111',
      verificationPlans: [planJson({ testCases: [testCaseJson()] })],
      currentVerificationAttemptId: '55555555-5555-4555-8555-555555555555',
      manualVerificationAttempts: [manualAttemptJson({ resultRevisions: revisions, currentCaseResults })],
    }))).toThrowError('The task response could not be validated safely.')
  })

  it('accepts authoritative manual eligibility for no-attempt and active-attempt states', () => {
    expect(decodeEngineeringTask(manualTaskJson()).verificationEligibility?.canStartVerificationAttempt).toBe(true)
    expect(decodeEngineeringTask(manualTaskJson({ eligibility: { canStartVerificationAttempt: false } }))
      .verificationEligibility?.canStartVerificationAttempt).toBe(false)
    const active = decodeEngineeringTask(manualTaskJson({ attempt: manualAttemptJson(), eligibility: {
      canStartVerificationAttempt: false, canRecordVerificationResult: false,
    } }))
    expect(active.currentVerificationAttemptId).toBe('55555555-5555-4555-8555-555555555555')
    expect(active.verificationEligibility?.canRecordVerificationResult).toBe(false)
  })

  it('accepts backend-coherent pass, fail, and terminal projections', () => {
    const passedResult = resultJson()
    const passEligible = decodeEngineeringTask(manualTaskJson({ attempt: manualAttemptJson({
      resultRevisions: [passedResult], currentCaseResults: [passedResult],
    }), eligibility: { canStartVerificationAttempt: false, canRecordVerificationResult: true,
      canCompleteVerificationPassed: true } }))
    expect(passEligible.verificationEligibility?.canCompleteVerificationPassed).toBe(true)

    const failedResult = resultJson({ result: 'Failed', failureDetails: { title: 'Mismatch',
      expectedResult: 'Expected.', actualResult: 'Actual.', reproductionSteps: ['Inspect.'],
      environmentNotes: [], errorMessage: null, evidenceDescriptions: [], severity: 'High' } })
    const failEligible = decodeEngineeringTask(manualTaskJson({ attempt: manualAttemptJson({
      resultRevisions: [failedResult], currentCaseResults: [failedResult],
    }), eligibility: { canStartVerificationAttempt: false, canRecordVerificationResult: true,
      canCompleteVerificationFailed: true } }))
    expect(failEligible.verificationEligibility?.canCompleteVerificationFailed).toBe(true)

    expect(decodeEngineeringTask(manualTaskJson({ status: 'ReadyForDelivery',
      attempt: completedAttempt('CompletedPassed') })).verificationEligibility?.readyForDelivery).toBe(true)
    expect(decodeEngineeringTask(manualTaskJson({ status: 'ManualVerificationFailed',
      attempt: completedAttempt('CompletedFailed') })).status).toBe('ManualVerificationFailed')
  })

  it.each([
    ['missing action boolean', (() => { const value = manualTaskJson()
      delete (value.verificationEligibility as Record<string, unknown>).canRecordVerificationResult
      return value })()],
    ['non-boolean action eligibility', manualTaskJson({ eligibility: { canRecordVerificationResult: 'yes' } })],
    ['historical InProgress attempt without pointer', manualTaskJson({ taskOverrides: {
      currentVerificationAttemptId: null, manualVerificationAttempts: [manualAttemptJson()] } })],
    ['historical CompletedPassed attempt without pointer', manualTaskJson({ taskOverrides: {
      currentVerificationAttemptId: null, manualVerificationAttempts: [completedAttempt('CompletedPassed')] } })],
    ['historical CompletedFailed attempt without pointer', manualTaskJson({ taskOverrides: {
      currentVerificationAttemptId: null, manualVerificationAttempts: [completedAttempt('CompletedFailed')] } })],
    ['nonempty history without pointer and Start disabled', manualTaskJson({
      eligibility: { canStartVerificationAttempt: false }, taskOverrides: {
        currentVerificationAttemptId: null, manualVerificationAttempts: [manualAttemptJson()] } })],
    ['start with active attempt', manualTaskJson({ attempt: manualAttemptJson(), eligibility: {
      canStartVerificationAttempt: true } })],
    ['record with no attempt', manualTaskJson({ eligibility: { canRecordVerificationResult: true } })],
    ['pass with no attempt', manualTaskJson({ eligibility: { canCompleteVerificationPassed: true } })],
    ['fail with no attempt', manualTaskJson({ eligibility: { canCompleteVerificationFailed: true } })],
    ['missing current plan', manualTaskJson({ taskOverrides: { currentVerificationPlanId: null } })],
    ['unresolved current plan', manualTaskJson({ taskOverrides: {
      currentVerificationPlanId: '99999999-9999-4999-8999-999999999999' } })],
    ['unresolved current attempt', manualTaskJson({ taskOverrides: {
      currentVerificationAttemptId: '99999999-9999-4999-8999-999999999999' } })],
    ['duplicate plan ID', manualTaskJson({ taskOverrides: { verificationPlans: [
      planJson({ testCases: [testCaseJson()] }), planJson({ planNumber: 2, testCases: [testCaseJson()] })] } })],
    ['duplicate attempt ID', manualTaskJson({ attempt: manualAttemptJson(), taskOverrides: {
      manualVerificationAttempts: [manualAttemptJson(), manualAttemptJson({ attemptNumber: 2 })] } })],
    ['attempt bound to another plan', manualTaskJson({ attempt: manualAttemptJson({
      verificationPlanId: '99999999-9999-4999-8999-999999999999' }) })],
    ['attempt bound to another revision', manualTaskJson({ attempt: manualAttemptJson({
      implementationRevisionId: '99999999-9999-4999-8999-999999999999' }) })],
    ['attempt bound to another plan fingerprint', manualTaskJson({ attempt: manualAttemptJson({
      verificationPlanFingerprint: '8'.repeat(64) }) })],
    ['attempt bound to another fingerprint', manualTaskJson({ attempt: manualAttemptJson({
      implementationResultFingerprint: '9'.repeat(64) }) })],
    ['completed attempt exposed as recordable', manualTaskJson({ attempt: completedAttempt('CompletedPassed'),
      eligibility: { canStartVerificationAttempt: false, canRecordVerificationResult: true } })],
    ['completed attempt is current while workflow still awaits verification', manualTaskJson({
      attempt: completedAttempt('CompletedPassed'), eligibility: { canStartVerificationAttempt: false } })],
    ['pass and fail both exposed', manualTaskJson({ attempt: manualAttemptJson({
      resultRevisions: [resultJson()], currentCaseResults: [resultJson()],
    }), eligibility: { canStartVerificationAttempt: false, canCompleteVerificationPassed: true,
      canCompleteVerificationFailed: true } })],
    ['ReadyForDelivery exposed as mutable', manualTaskJson({ status: 'ReadyForDelivery',
      attempt: completedAttempt('CompletedPassed'), eligibility: { canRecordVerificationResult: true } })],
    ['ManualVerificationFailed exposed as mutable', manualTaskJson({ status: 'ManualVerificationFailed',
      attempt: completedAttempt('CompletedFailed'), eligibility: { canCompleteVerificationFailed: true } })],
    ['terminal state missing current attempt', manualTaskJson({ status: 'ReadyForDelivery', attempt: null })],
  ])('rejects manual eligibility or pointer contradiction: %s', (_label, value) => {
    expect(() => decodeEngineeringTask(value)).toThrowError('The task response could not be validated safely.')
  })

  it('accepts an exact PullRequestCreated delivery and rejects malformed canonical URLs', () => {
    const proposal = {
      deliveryProposalId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', proposalNumber: 1,
      currentApprovedRevisionId: '22222222-2222-4222-8222-222222222222',
      currentImplementationResultFingerprint: 'a'.repeat(64),
      currentVerificationPlanId: '11111111-1111-4111-8111-111111111111',
      currentVerificationPlanFingerprint: 'e'.repeat(64),
      passedManualAttemptId: '55555555-5555-4555-8555-555555555555',
      passedManualAttemptFingerprint: 'f'.repeat(64), baseCommitSha: 'b'.repeat(40), remoteName: 'origin',
      gitHubRepositoryOwner: 'acme', gitHubRepositoryName: 'widget', targetBaseBranch: 'main',
      targetBaseCommitShaAtPreparation: 'b'.repeat(40), deliveryBranch: 'forge-delivery-328cbf18-r1',
      commitMessage: 'forge: deliver task 328cbf18 revision 1', pullRequestTitle: 'Forge AI: bounded change',
      pullRequestBody: 'Manual verification passed — user reported\nNo automated target validation was executed by Forge\nThis pull request was created by Forge and has not been merged',
      changedPaths: ['src/App.cs'], proposalFingerprint: 'c'.repeat(64), createdAt: '2026-07-20T12:20:00Z',
      status: 'Delivered', approvedAt: '2026-07-20T12:21:00Z',
    }
    const attempt = {
      attemptId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', attemptNumber: 1, commandId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
      deliveryProposalId: proposal.deliveryProposalId, deliveryProposalFingerprint: proposal.proposalFingerprint,
      startedAt: '2026-07-20T12:22:00Z', updatedAt: '2026-07-20T12:23:00Z', completedAt: '2026-07-20T12:23:00Z',
      leaseExpiresAt: '2026-07-20T12:27:00Z', phase: 'PullRequestCreated', commitSha: 'd'.repeat(40),
      remoteBranchSha: 'd'.repeat(40), pullRequestNumber: 23,
      pullRequestUrl: 'https://github.com/acme/widget/pull/23', safeFailureCategory: null, safeFailureMessage: null,
      recoveryRequired: false, activeCheckoutVerifiedBefore: true, activeCheckoutVerifiedAfter: true,
      legacyCanonicalizationUsed: false,
    }
    const value = manualTaskJson({ status: 'PullRequestCreated', attempt: completedAttempt('CompletedPassed'),
      eligibility: { readyForDelivery: false }, taskOverrides: {
        currentDeliveryProposalId: proposal.deliveryProposalId, currentDeliveryAttemptId: attempt.attemptId,
        deliveryProposals: [proposal], deliveryAttempts: [attempt], deliveryEligibility: {
          canPrepareDelivery: false, canApproveDelivery: false, canExecuteDelivery: false,
          canReconcileDelivery: false,
          deliveryRecoveryRequired: false, pullRequestCreated: true,
        },
      } })
    expect(decodeEngineeringTask(value).status).toBe('PullRequestCreated')
    expect(() => decodeEngineeringTask({ ...value, deliveryAttempts: [{ ...attempt,
      pullRequestUrl: 'https://example.invalid/acme/widget/pull/23' }] })).toThrowError(
      'The task response could not be validated safely.')
  })

  it('accepts bounded contiguous delivery retry history and rejects contradictory attempts', () => {
    const proposal = {
      deliveryProposalId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', proposalNumber: 1,
      currentApprovedRevisionId: '22222222-2222-4222-8222-222222222222', currentImplementationResultFingerprint: 'a'.repeat(64),
      currentVerificationPlanId: '11111111-1111-4111-8111-111111111111', currentVerificationPlanFingerprint: 'e'.repeat(64),
      passedManualAttemptId: '55555555-5555-4555-8555-555555555555', passedManualAttemptFingerprint: 'f'.repeat(64),
      baseCommitSha: 'b'.repeat(40), remoteName: 'origin', gitHubRepositoryOwner: 'acme', gitHubRepositoryName: 'widget',
      targetBaseBranch: 'main', targetBaseCommitShaAtPreparation: 'b'.repeat(40), deliveryBranch: 'forge-delivery-328cbf18-r1',
      commitMessage: 'forge: deliver task 328cbf18 revision 1', pullRequestTitle: 'Forge AI: bounded change',
      pullRequestBody: 'Manual verification passed â€” user reported\nNo automated target validation was executed by Forge\nThis pull request was created by Forge and has not been merged',
      changedPaths: ['src/App.cs'], proposalFingerprint: 'c'.repeat(64), createdAt: '2026-07-20T12:20:00Z',
      status: 'Delivered', approvedAt: '2026-07-20T12:21:00Z',
    }
    const failed = (number: number, id: string, commandId: string) => ({
      attemptId: id, attemptNumber: number, commandId, deliveryProposalId: proposal.deliveryProposalId,
      deliveryProposalFingerprint: proposal.proposalFingerprint, startedAt: `2026-07-20T12:2${number}:00Z`,
      updatedAt: `2026-07-20T12:2${number}:30Z`, completedAt: `2026-07-20T12:2${number}:30Z`,
      leaseExpiresAt: `2026-07-20T12:3${number}:00Z`, phase: 'FailedBeforeMutation', commitSha: null,
      remoteBranchSha: null, pullRequestNumber: null, pullRequestUrl: null,
      safeFailureCategory: 'delivery_failed_before_mutation', safeFailureMessage: 'Preflight failed safely.',
      recoveryRequired: false, activeCheckoutVerifiedBefore: false, activeCheckoutVerifiedAfter: false,
      legacyCanonicalizationUsed: false,
    })
    const successful = (number: number, id: string, commandId: string) => ({
      attemptId: id, attemptNumber: number, commandId, deliveryProposalId: proposal.deliveryProposalId,
      deliveryProposalFingerprint: proposal.proposalFingerprint, startedAt: '2026-07-20T12:32:00Z',
      updatedAt: '2026-07-20T12:33:00Z', completedAt: '2026-07-20T12:33:00Z',
      leaseExpiresAt: '2026-07-20T12:37:00Z', phase: 'PullRequestCreated', commitSha: 'd'.repeat(40),
      remoteBranchSha: 'd'.repeat(40), pullRequestNumber: 23,
      pullRequestUrl: 'https://github.com/acme/widget/pull/23', safeFailureCategory: null, safeFailureMessage: null,
      recoveryRequired: false, activeCheckoutVerifiedBefore: true, activeCheckoutVerifiedAfter: true,
      legacyCanonicalizationUsed: false,
    })
    const first = failed(1, '11111111-1111-4111-8111-111111111111', '21111111-1111-4111-8111-111111111111')
    const second = failed(2, '31111111-1111-4111-8111-111111111111', '41111111-1111-4111-8111-111111111111')
    const third = successful(3, '51111111-1111-4111-8111-111111111111', '61111111-1111-4111-8111-111111111111')
    const value = (attempts: unknown[], current: string, overrides: Record<string, unknown> = {}) => manualTaskJson({
      status: 'PullRequestCreated', attempt: completedAttempt('CompletedPassed'), eligibility: { readyForDelivery: false },
      taskOverrides: { currentDeliveryProposalId: proposal.deliveryProposalId, currentDeliveryAttemptId: current,
        deliveryProposals: [proposal], deliveryAttempts: attempts, deliveryEligibility: {
          canPrepareDelivery: false, canApproveDelivery: false, canExecuteDelivery: false,
          canReconcileDelivery: false,
          deliveryRecoveryRequired: false, pullRequestCreated: true,
        }, ...overrides },
    })

    expect(decodeEngineeringTask(value([{ ...third, attemptNumber: 1 }], third.attemptId)).deliveryAttempts).toHaveLength(1)
    expect(decodeEngineeringTask(value([first, { ...third, attemptNumber: 2 }], third.attemptId)).deliveryAttempts).toHaveLength(2)
    expect(decodeEngineeringTask(value([first, second, third], third.attemptId)).deliveryAttempts).toHaveLength(3)
    expect(() => decodeEngineeringTask(value([first, second, third, { ...third, attemptNumber: 4,
      attemptId: '71111111-1111-4111-8111-111111111111', commandId: '81111111-1111-4111-8111-111111111111' }], third.attemptId))).toThrow(/validated safely/)
    expect(() => decodeEngineeringTask(value([
      { ...first, phase: 'Prepared', completedAt: null, safeFailureCategory: null, safeFailureMessage: null },
      { ...second, phase: 'Prepared', completedAt: null, safeFailureCategory: null, safeFailureMessage: null },
      third,
    ], third.attemptId))).toThrow(/validated safely/)
    expect(() => decodeEngineeringTask(value([first, { ...third, attemptNumber: 2 }], first.attemptId))).toThrow(/validated safely/)
    expect(() => decodeEngineeringTask(value([first, { ...third, attemptNumber: 3 }], third.attemptId))).toThrow(/validated safely/)
    expect(() => decodeEngineeringTask(value([first, { ...third, attemptNumber: 2 }], third.attemptId, {
      deliveryEligibility: { canPrepareDelivery: false, canApproveDelivery: false, canExecuteDelivery: true,
        canReconcileDelivery: false,
        deliveryRecoveryRequired: false, pullRequestCreated: true },
    }))).toThrow(/validated safely/)
  })

  it('uses a separate fail-closed decoder for compact history', () => {
    expect(() => decodeEngineeringTaskSummaries([{ id: 'not-a-guid', secret: 'not exposed' }]))
      .toThrowError('The task-history response could not be validated safely.')
  })
})
