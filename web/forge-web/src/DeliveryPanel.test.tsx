// @vitest-environment jsdom

import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DeliveryPanel } from './DeliveryPanel'
import type { EngineeringTask } from './types'

const proposal = {
  deliveryProposalId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', proposalNumber: 1,
  currentApprovedRevisionId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', currentImplementationResultFingerprint: 'a'.repeat(64),
  currentVerificationPlanId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', currentVerificationPlanFingerprint: 'b'.repeat(64),
  passedManualAttemptId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', passedManualAttemptFingerprint: 'c'.repeat(64),
  baseCommitSha: 'd'.repeat(40), remoteName: 'origin' as const, gitHubRepositoryOwner: 'acme', gitHubRepositoryName: 'widget',
  targetBaseBranch: 'main' as const, targetBaseCommitShaAtPreparation: 'd'.repeat(40), deliveryBranch: 'forge-delivery-12345678-r2',
  commitMessage: 'forge: deliver task 12345678 revision 2', pullRequestTitle: 'Forge AI: bounded change',
  pullRequestBody: 'Manual verification passed — user reported\nNo automated target validation was executed by Forge\nThis pull request was created by Forge and has not been merged',
  changedPaths: ['src/App.cs'], proposalFingerprint: 'e'.repeat(64), createdAt: '2026-07-22T12:00:00Z',
  status: 'Prepared' as const, approvedAt: null,
}

function task(status: EngineeringTask['status'], overrides: Partial<EngineeringTask> = {}): EngineeringTask {
  return { id: '12345678-1234-4234-8234-123456789012', status, rowVersion: 10,
    deliveryProposals: status === 'ReadyForDelivery' ? [] : [proposal],
    currentDeliveryProposalId: status === 'ReadyForDelivery' ? null : proposal.deliveryProposalId,
    deliveryAttempts: [], currentDeliveryAttemptId: null,
    deliveryEligibility: { canPrepareDelivery: status === 'ReadyForDelivery',
      canApproveDelivery: status === 'AwaitingDeliveryApproval', canExecuteDelivery: false,
      canReconcileDelivery: status === 'DeliveryRecoveryRequired',
      deliveryRecoveryRequired: status === 'DeliveryRecoveryRequired', pullRequestCreated: status === 'PullRequestCreated' },
    ...overrides } as EngineeringTask
}

describe('DeliveryPanel', () => {
  let container: HTMLDivElement
  afterEach(() => container?.remove())
  async function render(selected: EngineeringTask) {
    container = document.createElement('div'); document.body.append(container)
    const root = createRoot(container); const handlers = { onPrepare: vi.fn(), onApprove: vi.fn(), onExecute: vi.fn(), onReconcile: vi.fn(), onExportTaskReport: vi.fn() }
    await act(async () => root.render(<DeliveryPanel task={selected} busy={false} activeAction={null} {...handlers} />))
    return { root, handlers }
  }

  it('prepares delivery from ReadyForDelivery', async () => {
    const view = await render(task('ReadyForDelivery'))
    expect([...container.querySelectorAll('button')].some(item => item.textContent === 'Reconcile existing delivery')).toBe(false)
    const button = [...container.querySelectorAll('button')].find(item => item.textContent === 'Prepare delivery')!
    await act(async () => button.click())
    expect(view.handlers.onPrepare).toHaveBeenCalledOnce()
  })

  it('requires checkbox and confirmation dialog before exact approval', async () => {
    const view = await render(task('AwaitingDeliveryApproval'))
    const checkbox = container.querySelector('input[type="checkbox"]') as HTMLInputElement
    await act(async () => { checkbox.click() })
    const review = [...container.querySelectorAll('button')].find(item => item.textContent === 'Review approval')!
    await act(async () => review.click())
    expect(container.querySelector('[role="dialog"]')).not.toBeNull()
    const approve = [...container.querySelectorAll('button')].find(item => item.textContent === 'Approve delivery proposal')!
    await act(async () => { approve.click(); approve.click() })
    expect(view.handlers.onApprove).toHaveBeenCalledOnce()
  })

  it('shows exact open PR and NOT MERGED without a merge action', async () => {
    const delivered = { ...proposal, status: 'Delivered' as const, approvedAt: '2026-07-22T12:01:00Z' }
    await render(task('PullRequestCreated', { deliveryProposals: [delivered], deliveryAttempts: [{
      attemptId: 'ffffffff-ffff-4fff-8fff-ffffffffffff', attemptNumber: 1, commandId: '11111111-1111-4111-8111-111111111111',
      deliveryProposalId: delivered.deliveryProposalId, deliveryProposalFingerprint: delivered.proposalFingerprint,
      startedAt: '2026-07-22T12:02:00Z', updatedAt: '2026-07-22T12:03:00Z', completedAt: '2026-07-22T12:03:00Z',
      leaseExpiresAt: '2026-07-22T12:07:00Z', phase: 'PullRequestCreated', commitSha: 'f'.repeat(40), remoteBranchSha: 'f'.repeat(40),
      pullRequestNumber: 23, pullRequestUrl: 'https://github.com/acme/widget/pull/23', safeFailureCategory: null,
      safeFailureMessage: null, recoveryRequired: false, activeCheckoutVerifiedBefore: true, activeCheckoutVerifiedAfter: true,
      legacyCanonicalizationUsed: false,
    }], currentDeliveryAttemptId: 'ffffffff-ffff-4fff-8fff-ffffffffffff' }))
    expect(container.textContent).toContain('PULL REQUEST CREATED')
    expect(container.textContent).toContain('NOT MERGED')
    expect(container.querySelector('a')?.href).toBe('https://github.com/acme/widget/pull/23')
    expect([...container.querySelectorAll('button')].some(button => /merge/i.test(button.textContent ?? ''))).toBe(false)
  })

  it('executes an approved proposal once and renders its durable phase', async () => {
    const approved = { ...proposal, status: 'Approved' as const, approvedAt: '2026-07-22T12:01:00Z' }
    const attempt = {
      attemptId: 'ffffffff-ffff-4fff-8fff-ffffffffffff', attemptNumber: 1, commandId: '11111111-1111-4111-8111-111111111111',
      deliveryProposalId: approved.deliveryProposalId, deliveryProposalFingerprint: approved.proposalFingerprint,
      startedAt: '2026-07-22T12:02:00Z', updatedAt: '2026-07-22T12:02:00Z', completedAt: null,
      leaseExpiresAt: '2026-07-22T12:07:00Z', phase: 'BranchPushed' as const, commitSha: 'f'.repeat(40),
      remoteBranchSha: 'f'.repeat(40), pullRequestNumber: null, pullRequestUrl: null,
      safeFailureCategory: null, safeFailureMessage: null, recoveryRequired: false,
      activeCheckoutVerifiedBefore: true, activeCheckoutVerifiedAfter: false, legacyCanonicalizationUsed: false,
    }
    const view = await render(task('AwaitingDeliveryApproval', { deliveryProposals: [approved],
      deliveryEligibility: { canPrepareDelivery: false, canApproveDelivery: false, canExecuteDelivery: true,
        canReconcileDelivery: false, deliveryRecoveryRequired: false, pullRequestCreated: false } }))
    const execute = [...container.querySelectorAll('button')]
      .find(item => item.textContent === 'Create commit, push branch, and open pull request')!
    await act(async () => { execute.click(); execute.click() })
    expect(view.handlers.onExecute).toHaveBeenCalledOnce()

    await act(async () => view.root.render(<DeliveryPanel task={task('Delivering', { deliveryProposals: [approved],
      deliveryAttempts: [attempt], currentDeliveryAttemptId: attempt.attemptId })} busy={false} activeAction={null}
      onPrepare={view.handlers.onPrepare} onApprove={view.handlers.onApprove} onExecute={view.handlers.onExecute}
      onReconcile={view.handlers.onReconcile}
      onExportTaskReport={view.handlers.onExportTaskReport} />))
    expect(container.textContent).toContain('Durable delivery phase: BranchPushed')
  })

  it('renders recovery as a safe stop with no blind retry', async () => {
    const attempt = {
      attemptId: 'ffffffff-ffff-4fff-8fff-ffffffffffff', attemptNumber: 1,
      commandId: '11111111-1111-4111-8111-111111111111', deliveryProposalId: proposal.deliveryProposalId,
      deliveryProposalFingerprint: proposal.proposalFingerprint, startedAt: '2026-07-22T12:02:00Z',
      updatedAt: '2026-07-22T12:03:00Z', completedAt: '2026-07-22T12:03:00Z', leaseExpiresAt: '2026-07-22T12:07:00Z',
      phase: 'RecoveryRequired' as const, commitSha: 'f'.repeat(40), remoteBranchSha: 'f'.repeat(40),
      pullRequestNumber: null, pullRequestUrl: null, safeFailureCategory: 'delivery_recovery_required',
      safeFailureMessage: 'Safe recovery required.', recoveryRequired: true, activeCheckoutVerifiedBefore: true,
      activeCheckoutVerifiedAfter: false, legacyCanonicalizationUsed: false,
    }
    const view = await render(task('DeliveryRecoveryRequired', { deliveryAttempts: [attempt], currentDeliveryAttemptId: attempt.attemptId }))
    expect(container.querySelector('[role="alert"]')?.textContent).toContain('will not blindly retry')
    expect([...container.querySelectorAll('button')].some(button => /retry/i.test(button.textContent ?? ''))).toBe(false)
    expect([...container.querySelectorAll('button')].some(button => button.textContent === 'Create commit, push branch, and open pull request')).toBe(false)
    const reconcile = [...container.querySelectorAll('button')].find(button => button.textContent === 'Reconcile existing delivery')!
    await act(async () => { reconcile.click(); reconcile.click() })
    expect(view.handlers.onReconcile).toHaveBeenCalledOnce()
  })
})
