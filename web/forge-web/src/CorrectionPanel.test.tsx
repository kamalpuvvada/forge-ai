// @vitest-environment jsdom

import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { CorrectionPanel } from './CorrectionPanel'
import type { EngineeringTask, FailureClassification } from './types'

const analysis = {
  analysisId: '11111111-1111-4111-8111-111111111111', analysisNumber: 1,
  classification: 'ImplementationDefect' as const, confidencePercent: 85,
  rootCauseSummary: 'The approved implementation operation did not satisfy the observed behavior.',
  rationale: 'The failed result references the exact approved mutating path.', evidenceReferences: ['failed-result:1'],
  affectedApprovedOperations: [{ path: 'src/App.cs', action: 'Modify' as const }],
  correctionStrategy: 'Adjust the approved file without expanding scope.', expectedBehavior: 'The prior failure no longer occurs.',
  verificationImpact: 'Repeat the failed case as a required regression.', risks: ['Manual verification remains required.'],
  source: 'DeterministicFake' as const, model: null, reasoningEffort: null, analysisFingerprint: 'a'.repeat(64),
  status: 'Completed', createdAt: '2026-07-21T12:00:00Z', trustLabel: 'FORGE GENERATED',
  safeRoute: 'Correction proposal available after explicit approval.',
}
const proposal = {
  proposalId: '22222222-2222-4222-8222-222222222222', proposalNumber: 1,
  analysisId: analysis.analysisId, analysisFingerprint: analysis.analysisFingerprint,
  failedAttemptId: '33333333-3333-4333-8333-333333333333', failedAttemptFingerprint: 'b'.repeat(64),
  previousApprovedRevisionId: '44444444-4444-4444-8444-444444444444', previousResultFingerprint: 'c'.repeat(64),
  approvedRequirementFingerprint: 'd'.repeat(64), approvedPlanFingerprint: 'e'.repeat(64), originalBaseCommitSha: 'f'.repeat(40),
  affectedApprovedOperations: analysis.affectedApprovedOperations, rootCauseSummary: analysis.rootCauseSummary,
  correctionStrategy: analysis.correctionStrategy, expectedBehavior: analysis.expectedBehavior,
  verificationImpact: analysis.verificationImpact, risks: analysis.risks, proposalFingerprint: '1'.repeat(64),
  status: 'AwaitingApproval' as const, createdAt: '2026-07-21T12:01:00Z', approvedAt: null,
}

function task(status: EngineeringTask['status'], classification: FailureClassification = analysis.classification) {
  return {
    id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    status, failureAnalyses: [{ ...analysis, classification,
      safeRoute: classification === 'ImplementationDefect' ? analysis.safeRoute : 'This route stops safely.' }],
    correctionProposals: classification === 'ImplementationDefect' ? [proposal] : [],
    currentFailureAnalysisId: analysis.analysisId,
    currentCorrectionProposalId: classification === 'ImplementationDefect' ? proposal.proposalId : null,
    correctionEligibility: {
      canGenerateFailureAnalysis: status === 'ManualVerificationFailed',
      canApproveCorrection: status === 'AwaitingCorrectionApproval', canGenerateCorrection: status === 'CorrectionApproved',
      canApproveCorrectedRevision: false, canGenerateReplacementVerificationPlan: false,
    },
  } as unknown as EngineeringTask
}

let container: HTMLDivElement | null = null
afterEach(() => { container?.remove(); container = null })

async function render(selected: EngineeringTask, activeAction: string | null = null) {
  container = document.createElement('div'); document.body.append(container)
  const props = { task: selected, busy: activeAction !== null, activeAction, onGenerateAnalysis: vi.fn(), onApproveProposal: vi.fn(),
    onGenerateCorrection: vi.fn(), onReconcileFailureAnalysis: vi.fn(), onReconcileCorrection: vi.fn() }
  const root = createRoot(container)
  await act(async () => root.render(<CorrectionPanel {...props} />))
  return { ...props, root }
}

describe('CorrectionPanel', () => {
  it.each([
    ['ManualVerificationFailed', 'failure-analysis', 'Generating fix analysis…'],
    ['AwaitingCorrectionApproval', 'correction-approval', 'Approving correction proposal…'],
    ['CorrectionApproved', 'correction-generation', 'Generating corrected implementation…'],
  ] as const)('shows truthful pending feedback for %s', async (status, activeAction, label) => {
    const rendered = await render(task(status), activeAction)
    const button = Array.from(container!.querySelectorAll('button')).find(item => item.textContent === label)
    expect(button?.getAttribute('aria-busy')).toBe('true')
    expect(button?.disabled).toBe(true)
    await act(async () => rendered.root.unmount())
  })

  it('offers bounded failure analysis from the failed state', async () => {
    const props = await render(task('ManualVerificationFailed'))
    const button = container!.querySelector<HTMLButtonElement>('button')!
    expect(button.textContent).toContain('Generate fix analysis')
    await act(async () => button.click())
    expect(props.onGenerateAnalysis).toHaveBeenCalledOnce()
  })

  it('blocks a second correction revision with a bounded message', async () => {
    await render({ ...task('ManualVerificationFailed'), implementationRevisions: [{}, {}] } as EngineeringTask)
    expect(container!.textContent).toContain('A second correction revision is not supported in this submission build.')
    expect(container!.textContent).not.toContain('Generate fix analysis')
  })

  it('renders failure-analysis recovery as a terminal safe stop without a spinner', async () => {
    await render(task('FailureAnalysisRecoveryRequired'))
    expect(container!.textContent).toContain('Failure-analysis recovery required')
    expect(container!.textContent).toContain('Revision 1 remains effective')
    expect(container!.querySelector('.spinner')).toBeNull()
  })

  it('offers provider-free reconciliation after an active failure-analysis lease expires', async () => {
    const selected = { ...task('FailureAnalysisPending'), failureAnalysisGenerationAttempts: [{
      commandId: '99999999-9999-4999-8999-999999999999', leaseExpiresAt: '2020-01-01T00:00:00Z',
      status: 'Prepared',
    }] } as unknown as EngineeringTask
    const rendered = await render(selected)
    const button = Array.from(container!.querySelectorAll('button')).find(item =>
      item.textContent?.includes('Reconcile failure analysis'))!
    expect(button).toBeTruthy()
    expect(container!.querySelector('.spinner')).toBeNull()
    await act(async () => button.click())
    expect(rendered.onReconcileFailureAnalysis).toHaveBeenCalledOnce()
  })

  it('renders correction recovery as a terminal safe stop without retry controls', async () => {
    await render(task('CorrectionRecoveryRequired'))
    expect(container!.textContent).toContain('Correction recovery required')
    expect(container!.textContent).toContain('will not retry provider dispatch')
    expect(container!.querySelector('.spinner')).toBeNull()
    expect(container!.textContent).not.toContain('Generate correction revision 2')
  })

  it('safe-stops unsupported classifications without a correction action', async () => {
    await render(task('AwaitingFailureResolution', 'EnvironmentOrSetupIssue'))
    expect(container!.textContent).toContain('Safe stop')
    expect(container!.textContent).not.toContain('Approve correction proposal')
  })

  it('requires explicit proposal confirmation before approval', async () => {
    const props = await render(task('AwaitingCorrectionApproval'))
    const button = Array.from(container!.querySelectorAll('button')).find(item => item.textContent?.includes('Approve correction'))!
    expect(button.disabled).toBe(true)
    const checkbox = container!.querySelector<HTMLInputElement>('input[type=checkbox]')!
    await act(async () => checkbox.click())
    expect(button.disabled).toBe(false)
    await act(async () => button.click())
    expect(props.onApproveProposal).toHaveBeenCalledOnce()
  })

  it('resets confirmation when the proposal or task binding changes', async () => {
    const rendered = await render(task('AwaitingCorrectionApproval'))
    const checkbox = container!.querySelector<HTMLInputElement>('input[type=checkbox]')!
    await act(async () => checkbox.click())
    expect(checkbox.checked).toBe(true)
    const replacement = { ...task('AwaitingCorrectionApproval'), id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
      correctionProposals: [{ ...proposal, proposalId: '55555555-5555-4555-8555-555555555555', proposalFingerprint: '2'.repeat(64) }],
      currentCorrectionProposalId: '55555555-5555-4555-8555-555555555555' }
    await act(async () => rendered.root.render(<CorrectionPanel task={replacement} busy={false}
      onGenerateAnalysis={rendered.onGenerateAnalysis} onApproveProposal={rendered.onApproveProposal}
      onGenerateCorrection={rendered.onGenerateCorrection} onReconcileFailureAnalysis={rendered.onReconcileFailureAnalysis}
      onReconcileCorrection={rendered.onReconcileCorrection} />))
    expect(container!.querySelector<HTMLInputElement>('input[type=checkbox]')!.checked).toBe(false)
  })

  it('resets confirmation when the same proposal receives a replacement fingerprint', async () => {
    const rendered = await render(task('AwaitingCorrectionApproval'))
    await act(async () => container!.querySelector<HTMLInputElement>('input[type=checkbox]')!.click())
    const replacement = { ...task('AwaitingCorrectionApproval'),
      correctionProposals: [{ ...proposal, proposalFingerprint: '3'.repeat(64) }] }
    await act(async () => rendered.root.render(<CorrectionPanel task={replacement} busy={false}
      onGenerateAnalysis={rendered.onGenerateAnalysis} onApproveProposal={rendered.onApproveProposal}
      onGenerateCorrection={rendered.onGenerateCorrection} onReconcileFailureAnalysis={rendered.onReconcileFailureAnalysis}
      onReconcileCorrection={rendered.onReconcileCorrection} />))
    expect(container!.querySelector<HTMLInputElement>('input[type=checkbox]')!.checked).toBe(false)
  })

  it('dispatches rapid correction approval at most once', async () => {
    const rendered = await render(task('AwaitingCorrectionApproval'))
    await act(async () => container!.querySelector<HTMLInputElement>('input[type=checkbox]')!.click())
    const button = Array.from(container!.querySelectorAll('button')).find(item => item.textContent?.includes('Approve correction'))!
    await act(async () => { button.click(); button.click() })
    expect(rendered.onApproveProposal).toHaveBeenCalledOnce()
  })

  it('shows provenance, the effective revision, exact scope, and correction generation action', async () => {
    const props = await render(task('CorrectionApproved'))
    expect(container!.textContent).toContain('Source: Deterministic Fake')
    expect(container!.textContent).toContain('Revision 1 remains EFFECTIVE')
    expect(container!.textContent).toContain('src/App.cs')
    expect(container!.textContent).toContain('Manual verification remains required.')
    const button = Array.from(container!.querySelectorAll('button'))
      .find(item => item.textContent?.includes('Generate correction revision 2'))!
    await act(async () => button.click())
    expect(props.onGenerateCorrection).toHaveBeenCalledOnce()
  })
})
