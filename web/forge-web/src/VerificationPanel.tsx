import { useEffect, useMemo, useRef, useState } from 'react'
import type { EngineeringTask, SystemCapabilities, VerificationCaseResult, VerificationFailureDetails, VerificationTestCase } from './types'
import { invalidVerificationEligibilityMessage } from './api'
import { blankCaseFormValue, isCaseFormValueValid } from './verificationForm'
import type { CaseFormValue } from './verificationForm'

interface Props {
  task: EngineeringTask
  capabilities: SystemCapabilities | null
  busy: boolean
  onGenerate: () => void
  onStart: () => void
  onSaveCase: (testCase: VerificationTestCase, value: CaseFormValue) => void
  onComplete: (passed: boolean, summary: string, confirmedByHuman: boolean) => void
  onExportPlan: () => void
}

export function VerificationPanel({ task, capabilities, busy, onGenerate, onStart, onSaveCase, onComplete, onExportPlan }: Props) {
  const plans = task.verificationPlans ?? []
  const attempts = task.manualVerificationAttempts ?? []
  const plan = plans.find(item => item.planId === task.currentVerificationPlanId) ?? null
  const attempt = attempts.find(item => item.attemptId === task.currentVerificationAttemptId) ?? null
  const actionGuard = useRef(false)
  const wasBusy = useRef(busy)
  const guardedRowVersion = useRef(task.rowVersion)
  useEffect(() => {
    if (wasBusy.current && !busy || guardedRowVersion.current !== task.rowVersion) actionGuard.current = false
    wasBusy.current = busy
    guardedRowVersion.current = task.rowVersion
  }, [busy, task.rowVersion])
  const [selectedCaseId, setSelectedCaseId] = useState<string | null>(null)
  const selectedCase = plan?.testCases.find(item => item.testCaseId === selectedCaseId) ?? plan?.testCases[0] ?? null
  const current = attempt?.currentCaseResults.find(item => item.testCaseId === selectedCase?.testCaseId)
  const initial = useMemo<CaseFormValue>(() => current ? {
    result: current.result, notes: current.notes ?? '', actualResult: current.actualResult ?? '',
    evidenceDescriptions: current.evidenceDescriptions, notApplicableReason: current.notApplicableReason ?? '',
    failureDetails: current.failureDetails,
  } : blankCaseFormValue, [current])
  const [draft, setDraft] = useState<CaseFormValue | null>(null)
  const value = draft ?? initial
  const [confirmed, setConfirmed] = useState(false)
  const [summary, setSummary] = useState('')
  const currentResults = attempt?.currentCaseResults ?? []
  const completedRequired = plan?.testCases.filter(item => item.isRequired &&
    ['Passed', 'NotApplicable'].includes(currentResults.find(result => result.testCaseId === item.testCaseId)?.result ?? '')).length ?? 0
  const requiredCount = plan?.testCases.filter(item => item.isRequired).length ?? 0
  const eligibility = task.verificationEligibility
  const canStart = eligibility?.canStartVerificationAttempt === true &&
    eligibility.canRecordVerificationResult === false &&
    eligibility.canCompleteVerificationPassed === false &&
    eligibility.canCompleteVerificationFailed === false &&
    plan?.status === 'Current' && task.currentVerificationAttemptId === null &&
    attempt === null && attempts.length === 0 && !busy
  const canSave = eligibility?.canRecordVerificationResult === true && plan?.status === 'Current' &&
    attempt?.status === 'InProgress' && selectedCase !== null &&
    plan.testCases.some(testCase => testCase.testCaseId === selectedCase.testCaseId) &&
    isCaseFormValueValid(value) && !busy
  const canCompletePassed = eligibility?.canCompleteVerificationPassed === true && plan?.status === 'Current' &&
    attempt?.status === 'InProgress' && confirmed && !busy
  const canCompleteFailed = eligibility?.canCompleteVerificationFailed === true && plan?.status === 'Current' &&
    attempt?.status === 'InProgress' && confirmed && !busy
  const invokeOnce = (allowed: boolean, action: () => void) => {
    if (!allowed || actionGuard.current) return
    actionGuard.current = true
    action()
  }
  const safeRetryStatus = ['FailedBeforeDispatch', 'RetryableProviderResponse', 'InterruptedBeforeDispatch']
    .includes(eligibility?.verificationGenerationStatus ?? '')
  const isInitial = eligibility?.canGenerateVerificationPlan === true &&
    eligibility.isInitialVerificationPlanGeneration === true &&
    eligibility.canRetryVerificationPlanGeneration === false &&
    eligibility.verificationGenerationStatus === 'NotStarted'
  const canRetry = eligibility?.canGenerateVerificationPlan === true &&
    eligibility.canRetryVerificationPlanGeneration === true &&
    eligibility.isInitialVerificationPlanGeneration === false && safeRetryStatus
  const canGenerate = isInitial || canRetry
  const eligibilityMessage = eligibility?.verificationGenerationStatusMessage ??
    (!canGenerate ? invalidVerificationEligibilityMessage : null)

  if (task.status === 'ImplementationApproved' || task.status === 'VerificationPlanning') return <section className="verification-panel">
    <p className="eyebrow">07 · MANUAL VERIFICATION</p>
    <h2>Generate a manual verification plan</h2>
    <p>Forge will generate guidance for the exact approved revision. It will run no command, acquire no worktree lock, and perform no Git action.</p>
    <div className="verification-trust" role="note"><strong>MANUAL — NOT EXECUTED BY FORGE</strong><span>{capabilities?.verificationPlanningProvider ?? capabilities?.planningProvider ?? 'Unavailable'} · {capabilities?.verificationPlanningModel ?? 'unavailable'} · {capabilities?.verificationPlanningReasoningEffort ?? 'unavailable'} reasoning</span><small>OpenAI API usage is billed separately when OpenAI mode is selected.</small></div>
    {eligibilityMessage && <p role="status">{eligibilityMessage}</p>}
    <button type="button" className="primary-button compact" disabled={busy || capabilities?.verificationPlanningConfigured === false || !canGenerate} onClick={onGenerate}>{canRetry ? 'Retry verification-plan generation' : isInitial ? 'Generate manual verification plan' : 'Verification generation unavailable'}</button>
  </section>

  if (!plan) return null
  if (task.status === 'ReadyForDelivery') return <section className="verification-panel ready-view">
    <p className="eyebrow">READY FOR DELIVERY</p><h2>Manual verification passed — user reported</h2>
    <p>The exact approved implementation revision received a bound plan and explicit human pass confirmation.</p>
    <code>{attempt?.attemptFingerprint}</code><small>No automated validation, commit, push, or pull request was performed.</small>
    <button type="button" className="secondary-button" onClick={onExportPlan}>Download verification plan PDF</button>
  </section>

  if (task.status === 'ManualVerificationFailed') return <section className="verification-panel planning-failure">
    <p className="eyebrow">MANUAL VERIFICATION FAILED · USER REPORTED</p><h2>Failed or blocked outcomes were preserved</h2>
    {attempt?.currentCaseResults.filter(item => ['Failed', 'Blocked'].includes(item.result)).map(item => <article key={item.testCaseId}><strong>{plan.testCases.find(testCase => testCase.testCaseId === item.testCaseId)?.title}</strong><p>{item.failureDetails?.actualResult}</p></article>)}
    <p>Failure analysis and implementation correction are unavailable in this slice.</p>
    <button type="button" className="secondary-button" onClick={onExportPlan}>Download verification plan PDF</button>
  </section>

  return <section className="verification-panel">
    <p className="eyebrow">MANUAL VERIFICATION · USER REPORTED</p><h2>{plan.summary}</h2><p>{plan.scope}</p>
    <div className="verification-trust"><strong>{plan.trustLabel}</strong><span>{plan.executionLabel}</span></div>
    <dl><div><dt>Revision</dt><dd><code>{plan.implementationRevisionId}</code></dd></div><div><dt>Result fingerprint</dt><dd><code>{plan.implementationResultFingerprint}</code></dd></div></dl>
    {plan.preconditions.length > 0 && <><h3>Preconditions</h3><ul>{plan.preconditions.map(item => <li key={item}>{item}</li>)}</ul></>}
    <div className="export-actions"><button type="button" className="secondary-button" onClick={onExportPlan}>Download verification plan PDF</button>{!attempt && <button type="button" className="primary-button compact" disabled={!canStart} onClick={() => invokeOnce(canStart, onStart)}>Start verification</button>}</div>
    {attempt && attempt.status === 'InProgress' && <>
      <p aria-live="polite"><strong>{completedRequired} of {requiredCount} required cases acceptable</strong></p>
      <div className="verification-case-tabs" role="list" aria-label="Verification cases">{plan.testCases.map(testCase => <button type="button" key={testCase.testCaseId} onClick={() => { setSelectedCaseId(testCase.testCaseId); setDraft(null) }} aria-current={selectedCase?.testCaseId === testCase.testCaseId ? 'true' : undefined}>{testCase.order}. {testCase.title} · {currentResults.find(item => item.testCaseId === testCase.testCaseId)?.result ?? 'NotStarted'} {testCase.isRequired ? '(required)' : '(optional)'}</button>)}</div>
      {selectedCase && <form className="verification-case" onSubmit={event => { event.preventDefault(); invokeOnce(canSave, () => onSaveCase(selectedCase, value)) }}>
        <fieldset><legend>Record case {selectedCase.order}: {selectedCase.title}</legend><p>{selectedCase.objective}</p>
          <ol>{selectedCase.orderedSteps.map(step => <li key={step.order}><strong>MANUAL — NOT EXECUTED BY FORGE:</strong> {step.instruction}{step.approvedValidationCommandId && <code>{step.approvedValidationCommandId}</code>}<small>Expected: {step.expectedObservation}</small></li>)}</ol>
          <p><strong>Expected result:</strong> {selectedCase.expectedResult}</p>
          <label>Result<select value={value.result} onChange={event => setDraft({ ...value, result: event.target.value as VerificationCaseResult })}><option>NotStarted</option><option>Passed</option><option>Failed</option><option>Blocked</option><option>NotApplicable</option></select></label>
          <label>Actual result<textarea maxLength={2000} value={value.actualResult} onChange={event => setDraft({ ...value, actualResult: event.target.value })} /></label>
          <label>Notes<textarea maxLength={2000} value={value.notes} onChange={event => setDraft({ ...value, notes: event.target.value })} /></label>
          <label>Evidence descriptions (one per line)<textarea value={value.evidenceDescriptions.join('\n')} onChange={event => setDraft({ ...value, evidenceDescriptions: event.target.value.split('\n').filter(Boolean).slice(0, 6) })} /></label>
          {value.result === 'NotApplicable' && <label>Reason required<textarea required value={value.notApplicableReason} onChange={event => setDraft({ ...value, notApplicableReason: event.target.value })} /></label>}
          {['Failed', 'Blocked'].includes(value.result) && <FailureFields value={value} setValue={setDraft} expected={selectedCase.expectedResult} />}
          <button className="primary-button compact" disabled={!canSave}>Save case result</button>
        </fieldset>
      </form>}
      <details><summary>Append-only result history</summary>{attempt.resultRevisions.map(item => <p key={item.resultRevisionId}>Revision {item.revisionNumber} · {plan.testCases.find(testCase => testCase.testCaseId === item.testCaseId)?.title} · {item.result} · USER REPORTED</p>)}</details>
      <fieldset className="verification-completion"><legend>Complete manual verification</legend><label><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /> I confirm these outcomes were recorded by a human.</label><label>Completion summary<textarea maxLength={2000} value={summary} onChange={event => setSummary(event.target.value)} /></label><div className="export-actions"><button type="button" className="primary-button compact" disabled={!canCompletePassed} onClick={() => { if (canCompletePassed && window.confirm('Complete this attempt as passed?')) invokeOnce(true, () => onComplete(true, summary, confirmed)) }}>Complete as passed</button><button type="button" className="secondary-button" disabled={!canCompleteFailed} onClick={() => { if (canCompleteFailed && window.confirm('Complete this attempt as failed?')) invokeOnce(true, () => onComplete(false, summary, confirmed)) }}>Complete as failed</button></div></fieldset>
    </>}
  </section>
}

function FailureFields({ value, setValue, expected }: { value: CaseFormValue; setValue: (value: CaseFormValue) => void; expected: string }) {
  const failure = value.failureDetails ?? { title: '', expectedResult: expected, actualResult: value.actualResult, reproductionSteps: [], environmentNotes: [], errorMessage: null, evidenceDescriptions: value.evidenceDescriptions, severity: 'Medium' as const }
  const update = (patch: Partial<VerificationFailureDetails>) => setValue({ ...value, failureDetails: { ...failure, ...patch } })
  return <fieldset><legend>Failure details required</legend><label>Title<input required maxLength={160} value={failure.title} onChange={event => update({ title: event.target.value })} /></label><label>Actual result<textarea required maxLength={2000} value={failure.actualResult} onChange={event => update({ actualResult: event.target.value })} /></label><label>Reproduction steps (one per line)<textarea required value={failure.reproductionSteps.join('\n')} onChange={event => update({ reproductionSteps: event.target.value.split('\n').filter(Boolean).slice(0, 12) })} /></label><label>Environment notes (one per line)<textarea value={failure.environmentNotes.join('\n')} onChange={event => update({ environmentNotes: event.target.value.split('\n').filter(Boolean).slice(0, 8) })} /></label><label>Error message<textarea maxLength={2000} value={failure.errorMessage ?? ''} onChange={event => update({ errorMessage: event.target.value || null })} /></label><label>Severity<select value={failure.severity} onChange={event => update({ severity: event.target.value as VerificationFailureDetails['severity'] })}><option>Low</option><option>Medium</option><option>High</option><option>Critical</option></select></label></fieldset>
}
