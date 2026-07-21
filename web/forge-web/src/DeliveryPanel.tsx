import { useEffect, useRef, useState } from 'react'
import type { EngineeringTask } from './types'

interface Props {
  task: EngineeringTask
  busy: boolean
  activeAction: string | null
  onPrepare: () => void
  onApprove: () => void
  onExecute: () => void
  onReconcile: () => void
  onExportTaskReport: () => void
}

export function DeliveryPanel({ task, busy, activeAction, onPrepare, onApprove, onExecute, onReconcile, onExportTaskReport }: Props) {
  const proposal = (task.deliveryProposals ?? []).find(item => item.deliveryProposalId === task.currentDeliveryProposalId) ?? null
  const attempt = (task.deliveryAttempts ?? []).find(item => item.attemptId === task.currentDeliveryAttemptId) ?? null
  const [confirmed, setConfirmed] = useState(false)
  const [dialogOpen, setDialogOpen] = useState(false)
  const approvalGuard = useRef(false)
  const executionGuard = useRef(false)
  const reconciliationGuard = useRef(false)
  useEffect(() => { setConfirmed(false); setDialogOpen(false); approvalGuard.current = false; executionGuard.current = false; reconciliationGuard.current = false }, [task.id, proposal?.proposalFingerprint, task.status])
  useEffect(() => { if (!busy) { approvalGuard.current = false; executionGuard.current = false; reconciliationGuard.current = false } }, [busy])

  const approve = () => {
    if (!confirmed || busy || approvalGuard.current || !task.deliveryEligibility?.canApproveDelivery) return
    approvalGuard.current = true; setDialogOpen(false); onApprove()
  }
  const execute = () => {
    if (busy || executionGuard.current || !task.deliveryEligibility?.canExecuteDelivery) return
    executionGuard.current = true; onExecute()
  }
  const reconcile = () => {
    if (busy || reconciliationGuard.current || !task.deliveryEligibility?.canReconcileDelivery) return
    reconciliationGuard.current = true; onReconcile()
  }

  return <section className="delivery-panel" aria-labelledby="delivery-title">
    <p className="eyebrow">08 · GITHUB DELIVERY</p>
    <h2 id="delivery-title">Deterministic commit, branch, and pull request</h2>
    <p className="verification-boundary" role="note">No automated target validation was performed. Forge will not merge, force-push, or push to main.</p>
    {task.status === 'ReadyForDelivery' && <div className="delivery-action">
      <p>Run read-only GitHub.com, origin, main, active-checkout, and approved-worktree preflight.</p>
      <button type="button" className="primary-button compact" disabled={busy || !task.deliveryEligibility?.canPrepareDelivery}
        aria-busy={activeAction === 'delivery-prepare'} onClick={onPrepare}>{activeAction === 'delivery-prepare' ? 'Preparing delivery proposal…' : 'Prepare delivery'}</button>
    </div>}
    {proposal && <article className="delivery-proposal">
      <div className="delivery-heading"><strong>Delivery proposal {proposal.proposalNumber}</strong><span>{proposal.status}</span></div>
      <dl className="delivery-metadata">
        <div><dt>Repository</dt><dd>{proposal.gitHubRepositoryOwner}/{proposal.gitHubRepositoryName}</dd></div>
        <div><dt>Remote</dt><dd>{proposal.remoteName}</dd></div>
        <div><dt>Base</dt><dd>{proposal.targetBaseBranch} · <code>{proposal.targetBaseCommitShaAtPreparation}</code></dd></div>
        <div><dt>Delivery branch</dt><dd><code>{proposal.deliveryBranch}</code></dd></div>
        <div><dt>Commit message</dt><dd>{proposal.commitMessage}</dd></div>
        <div><dt>PR title</dt><dd>{proposal.pullRequestTitle}</dd></div>
        <div><dt>Approved revision</dt><dd>{proposal.currentApprovedRevisionId}</dd></div>
        <div><dt>Passed attempt</dt><dd>{proposal.passedManualAttemptId}</dd></div>
      </dl>
      <details><summary>Review exact pull-request body and changed paths</summary><pre className="delivery-body">{proposal.pullRequestBody}</pre><ul>{proposal.changedPaths.map(path => <li key={path}><code>{path}</code></li>)}</ul></details>
      <p><strong>Manual verification passed — user reported</strong></p>
      <code aria-label="Delivery proposal fingerprint">{proposal.proposalFingerprint}</code>
      {task.deliveryEligibility?.canApproveDelivery && <div className="approval-row">
        <label><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> I approve this exact commit, branch, repository, base branch, and pull-request metadata.</label>
        <button type="button" className="primary-button compact" disabled={busy || !confirmed} onClick={() => setDialogOpen(true)}>Review approval</button>
      </div>}
      {dialogOpen && <div className="approval-dialog approval-dialog-fallback" role="dialog" aria-modal="true" aria-labelledby="delivery-approval-title" onKeyDown={event => { if (event.key === 'Escape') setDialogOpen(false) }}>
        <h3 id="delivery-approval-title">Approve exact delivery proposal?</h3><p>This approval performs no Git or GitHub mutation.</p>
        <div className="dialog-actions"><button type="button" className="secondary-button" onClick={() => setDialogOpen(false)}>Cancel</button><button type="button" className="primary-button compact" autoFocus onClick={approve}>Approve delivery proposal</button></div>
      </div>}
      {task.deliveryEligibility?.canExecuteDelivery && <button type="button" className="primary-button compact" disabled={busy}
        aria-busy={activeAction === 'delivery-execute'} onClick={execute}>{activeAction === 'delivery-execute' ? 'Creating approved delivery…' : 'Create commit, push branch, and open pull request'}</button>}
    </article>}
    {attempt && <article className="delivery-attempt" aria-live="polite"><strong>Durable delivery phase: {attempt.phase}</strong>{attempt.commitSha && <p>Commit <code>{attempt.commitSha}</code></p>}</article>}
    {task.status === 'PullRequestCreated' && proposal && attempt && <div className="delivery-complete">
      <p className="eyebrow">PULL REQUEST CREATED</p><h3>Delivery created successfully</h3>
      <p><strong>NOT MERGED</strong></p><p>Branch <code>{proposal.deliveryBranch}</code> into <code>main</code></p>
      <p>Commit <code>{attempt.commitSha}</code></p><p>Pull request #{attempt.pullRequestNumber}</p>
      {attempt.pullRequestUrl && <a className="primary-button compact" href={attempt.pullRequestUrl} target="_blank" rel="noreferrer">Open pull request</a>}
      {attempt.legacyCanonicalizationUsed && <p>Legacy Forge punctuation normalization was used during bounded read-only reconciliation.</p>}
    </div>}
    {task.status === 'DeliveryRecoveryRequired' && <div className="safe-stop" role="alert"><strong>Delivery recovery required</strong><br />External mutation may have occurred. Forge will not blindly retry, force-push, or create another pull request.
      <p>Verify and adopt the existing commit, branch, and pull request without creating or modifying any Git or GitHub object.</p>
      {task.deliveryEligibility?.canReconcileDelivery && <button type="button" className="secondary-button" disabled={busy}
        aria-busy={activeAction === 'delivery-reconcile'} onClick={reconcile}>{activeAction === 'delivery-reconcile' ? 'Reconciling existing delivery…' : 'Reconcile existing delivery'}</button>}
    </div>}
    <div className="export-actions"><button type="button" className="secondary-button" disabled={busy} onClick={onExportTaskReport}>Download task report PDF</button></div>
  </section>
}
