import { useEffect, useMemo, useRef, useState } from 'react'
import type { EngineeringTask } from './types'

interface Props {
  task: EngineeringTask
  busy: boolean
  activeAction?: string | null
  onGenerateAnalysis: () => void
  onApproveProposal: () => void
  onGenerateCorrection: () => void
  onReconcileFailureAnalysis: () => void
  onReconcileCorrection: () => void
}

export function CorrectionPanel({ task, busy, activeAction = null, onGenerateAnalysis, onApproveProposal, onGenerateCorrection,
  onReconcileFailureAnalysis, onReconcileCorrection }: Props) {
  const analysis = useMemo(() => (task.failureAnalyses ?? []).find(item =>
    item.analysisId === task.currentFailureAnalysisId) ?? null,
  [task.failureAnalyses, task.currentFailureAnalysisId])
  const proposal = useMemo(() => (task.correctionProposals ?? []).find(item =>
    item.proposalId === task.currentCorrectionProposalId) ?? null,
  [task.correctionProposals, task.currentCorrectionProposalId])
  const [confirmed, setConfirmed] = useState(false)
  const latestAnalysisAttempt = task.failureAnalysisGenerationAttempts?.at(-1) ?? null
  const latestCorrectionAttempt = task.correctionGenerationAttempts?.at(-1) ?? null
  const analysisLeaseExpired = latestAnalysisAttempt !== null && Date.parse(latestAnalysisAttempt.leaseExpiresAt) <= Date.now()
  const correctionLeaseExpired = latestCorrectionAttempt !== null && Date.parse(latestCorrectionAttempt.leaseExpiresAt) <= Date.now()
  const approvalGuard = useRef(false)
  const confirmationKey = `${task.id}:${proposal?.proposalId ?? 'none'}:${proposal?.proposalFingerprint ?? 'none'}`

  useEffect(() => {
    setConfirmed(false)
    approvalGuard.current = false
  }, [confirmationKey, task.status])

  useEffect(() => {
    if (!busy) approvalGuard.current = false
  }, [busy])

  const approveOnce = () => {
    if (busy || !confirmed || !task.correctionEligibility?.canApproveCorrection || approvalGuard.current) return
    approvalGuard.current = true
    onApproveProposal()
  }

  return <section className="correction-panel" aria-labelledby="correction-title">
    <p className="eyebrow">HUMAN VERIFICATION CORRECTION LOOP</p>
    <h2 id="correction-title">Fix analysis and governed correction</h2>
    {task.status === 'ManualVerificationFailed' && (task.implementationRevisions?.length ?? 1) > 1 && <p className="safe-stop" role="status">A second correction revision is not supported in this submission build.</p>}
    {task.status === 'ManualVerificationFailed' && (task.implementationRevisions?.length ?? 1) === 1 && <div className="correction-action">
      <p>The failed attempt and its user-reported evidence remain immutable. Forge can generate a bounded fix analysis.</p>
      <button type="button" className="primary-button compact" disabled={busy || !task.correctionEligibility?.canGenerateFailureAnalysis}
        aria-busy={activeAction === 'failure-analysis'} onClick={onGenerateAnalysis}>{activeAction === 'failure-analysis' ? 'Generating fix analysis…' : <>Generate fix analysis <span>→</span></>}</button>
    </div>}
    {task.status === 'FailureAnalysisPending' && analysisLeaseExpired && <div className="safe-stop" role="status"><p>The failure-analysis lease expired. Reconcile the durable checkpoints without another provider call.</p><button type="button" className="secondary-button" disabled={busy} onClick={onReconcileFailureAnalysis}>Reconcile failure analysis</button></div>}
    {task.status === 'FailureAnalysisPending' && !analysisLeaseExpired && <div role="status" className="implementation-progress"><span className="spinner dark" /><p>Generating bounded failure analysis. Forge will not modify either implementation revision.</p></div>}
    {task.status === 'FailureAnalysisRecoveryRequired' && <p className="safe-stop" role="alert"><strong>Failure-analysis recovery required</strong><br />Dispatch or response evidence was recorded, so Forge will not retry. Revision 1 remains effective and no delivery action is available.</p>}
    {analysis && <article className="failure-analysis">
      <div className="analysis-heading"><span className="fake-label">{analysis.trustLabel}</span><strong>{analysis.classification} · {analysis.confidencePercent}% confidence</strong></div>
      <p className="provider-line"><strong>Source:</strong> {analysis.source === 'DeterministicFake' ? 'Deterministic Fake' : 'OpenAI'} · <strong>Model:</strong> {analysis.model ?? 'not applicable'} · <strong>Reasoning:</strong> {analysis.reasoningEffort ?? 'not applicable'}</p>
      <dl>
        <div><dt>Root cause</dt><dd>{analysis.rootCauseSummary}</dd></div>
        <div><dt>Rationale</dt><dd>{analysis.rationale}</dd></div>
        <div><dt>Correction strategy</dt><dd>{analysis.correctionStrategy}</dd></div>
        <div><dt>Expected behavior</dt><dd>{analysis.expectedBehavior}</dd></div>
        <div><dt>Verification impact</dt><dd>{analysis.verificationImpact}</dd></div>
      </dl>
      <details><summary>Evidence, approved operations, and risks</summary>
        <h3>Evidence references</h3><ul>{analysis.evidenceReferences.map(item => <li key={item}>{item}</li>)}</ul>
        <h3>Affected approved operations</h3><ul>{analysis.affectedApprovedOperations.map(item => <li key={`${item.path}:${item.action}`}><code>{item.path}</code> · {item.action}</li>)}</ul>
        <h3>Risks</h3><ul>{analysis.risks.map(item => <li key={item}>{item}</li>)}</ul>
      </details>
      {analysis.classification !== 'ImplementationDefect' && <p className="safe-stop" role="status"><strong>Safe stop</strong><br />{analysis.safeRoute}</p>}
    </article>}
    {proposal && <article className="correction-proposal">
      <p className="eyebrow">CORRECTION PROPOSAL {proposal.proposalNumber}</p>
      <h3>Exact approved correction scope</h3>
      <p><strong>Revision 1 remains EFFECTIVE</strong> until revision 2 is generated, reviewed, and explicitly approved.</p>
      <table><thead><tr><th>Path</th><th>Action</th></tr></thead><tbody>{proposal.affectedApprovedOperations.map(item => <tr key={`${item.path}:${item.action}`}><td><code>{item.path}</code></td><td>{item.action}</td></tr>)}</tbody></table>
      <p>{proposal.correctionStrategy}</p><p><strong>Expected:</strong> {proposal.expectedBehavior}</p>
      <p><strong>Verification impact:</strong> {proposal.verificationImpact}</p>
      <h4>Risks</h4><ul>{proposal.risks.map(item => <li key={item}>{item}</li>)}</ul>
      <code aria-label="Correction proposal fingerprint">{proposal.proposalFingerprint}</code>
      {task.status === 'AwaitingCorrectionApproval' && <div className="approval-row">
        <label><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> I approve this exact correction scope for revision 2.</label>
        <button type="button" className="primary-button compact" disabled={busy || !confirmed || !task.correctionEligibility?.canApproveCorrection}
          aria-busy={activeAction === 'correction-approval'} onClick={approveOnce}>{activeAction === 'correction-approval' ? 'Approving correction proposal…' : <>Approve correction proposal <span>→</span></>}</button>
      </div>}
      {task.status === 'CorrectionApproved' && <button type="button" className="primary-button compact" disabled={busy || !task.correctionEligibility?.canGenerateCorrection}
        aria-busy={activeAction === 'correction-generation'} onClick={onGenerateCorrection}>{activeAction === 'correction-generation' ? 'Generating corrected implementation…' : <>Generate correction revision 2 <span>→</span></>}</button>}
    </article>}
    {task.status === 'ImplementingCorrection' && correctionLeaseExpired && <div className="safe-stop" role="status"><p>The correction-generation lease expired. Reconcile the persisted checkpoints without a provider call or filesystem mutation.</p><button type="button" className="secondary-button" disabled={busy} onClick={onReconcileCorrection}>Reconcile correction attempt</button></div>}
    {task.status === 'ImplementingCorrection' && !correctionLeaseExpired && <div role="status" className="implementation-progress"><span className="spinner dark" /><p>Generating revision 2 in its separate isolated worktree. Revision 1 remains effective.</p></div>}
    {task.status === 'CorrectionRecoveryRequired' && <p className="safe-stop" role="alert"><strong>Correction recovery required</strong><br />Forge will not retry provider dispatch or workspace mutation. Revision 1 remains effective and no delivery action is available.</p>}
  </section>
}
