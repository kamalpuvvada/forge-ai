import { useEffect, useMemo, useRef, useState } from 'react'
import type {
  EngineeringTask,
  ManualCaseResultRevision,
  ManualVerificationAttempt,
  SystemCapabilities,
  VerificationCaseResult,
  VerificationFailureDetails,
  VerificationPlan,
  VerificationTestCase,
} from './types'
import { invalidVerificationEligibilityMessage } from './api'
import { blankCaseFormValue, isCaseFormValueValid } from './verificationForm'
import type { CaseFormValue } from './verificationForm'

interface Props {
  task: EngineeringTask
  capabilities: SystemCapabilities | null
  busy: boolean
  documentsBusy?: boolean
  documentError?: string | null
  onGenerate: () => void
  onStart: () => void
  onSaveCase: (testCase: VerificationTestCase, value: CaseFormValue) => void
  onComplete: (passed: boolean, summary: string, confirmedByHuman: boolean) => void
  onExportPlan: () => void
  onExportApprovedPlan: () => void
  onExportTaskReport: () => void
}

const acceptableResults = new Set<VerificationCaseResult>(['Passed', 'NotApplicable'])

export function VerificationPanel({
  task,
  capabilities,
  busy,
  documentsBusy = false,
  documentError = null,
  onGenerate,
  onStart,
  onSaveCase,
  onComplete,
  onExportPlan,
  onExportApprovedPlan,
  onExportTaskReport,
}: Props) {
  const plans = useMemo(() => task.verificationPlans ?? [], [task.verificationPlans])
  const attempts = useMemo(() => task.manualVerificationAttempts ?? [], [task.manualVerificationAttempts])
  const plan = useMemo(() => plans.find(item => item.planId === task.currentVerificationPlanId) ?? null,
    [plans, task.currentVerificationPlanId])
  const attempt = useMemo(() => attempts.find(item => item.attemptId === task.currentVerificationAttemptId) ?? null,
    [attempts, task.currentVerificationAttemptId])
  const currentResults = useMemo(() => attempt?.currentCaseResults ?? [], [attempt])
  const initialCaseId = firstUnresolvedCaseId(plan, currentResults)
  const [expandedCaseId, setExpandedCaseId] = useState<string | null>(() => initialCaseId)
  const [draft, setDraft] = useState<CaseFormValue | null>(null)
  const [confirmed, setConfirmed] = useState(false)
  const [summary, setSummary] = useState('')
  const actionGuard = useRef(false)
  const wasBusy = useRef(busy)
  const guardedRowVersion = useRef(task.rowVersion)
  const contextKey = `${plan?.planId ?? 'none'}:${attempt?.attemptId ?? 'none'}`
  const previousContextKey = useRef(contextKey)
  const previousRevisionCount = useRef(attempt?.resultRevisions.length ?? 0)

  useEffect(() => {
    if (wasBusy.current && !busy || guardedRowVersion.current !== task.rowVersion) actionGuard.current = false
    wasBusy.current = busy
    guardedRowVersion.current = task.rowVersion
  }, [busy, task.rowVersion])

  useEffect(() => {
    if (previousContextKey.current !== contextKey) {
      previousContextKey.current = contextKey
      previousRevisionCount.current = attempt?.resultRevisions.length ?? 0
      setExpandedCaseId(firstUnresolvedCaseId(plan, currentResults))
      setDraft(null)
    }
  }, [attempt?.resultRevisions.length, contextKey, currentResults, plan])

  useEffect(() => {
    const revisionCount = attempt?.resultRevisions.length ?? 0
    if (revisionCount > previousRevisionCount.current && expandedCaseId) {
      const savedResult = currentResults.find(item => item.testCaseId === expandedCaseId)
      if (savedResult && acceptableResults.has(savedResult.result)) {
        setExpandedCaseId(nextUnresolvedCaseId(plan, currentResults, expandedCaseId))
      }
      setDraft(null)
    }
    previousRevisionCount.current = revisionCount
  }, [attempt?.resultRevisions.length, currentResults, expandedCaseId, plan])

  const selectedCase = plan?.testCases.find(item => item.testCaseId === expandedCaseId) ?? null
  const current = currentResults.find(item => item.testCaseId === selectedCase?.testCaseId)
  const initial = useMemo<CaseFormValue>(() => current ? {
    result: current.result,
    notes: current.notes ?? '',
    actualResult: current.actualResult ?? '',
    evidenceDescriptions: current.evidenceDescriptions,
    notApplicableReason: current.notApplicableReason ?? '',
    failureDetails: current.failureDetails,
  } : blankCaseFormValue, [current])
  const value = draft ?? initial
  const completedRequired = plan?.testCases.filter(item => item.isRequired &&
    acceptableResults.has(currentResults.find(result => result.testCaseId === item.testCaseId)?.result ?? 'NotStarted')).length ?? 0
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

  if (task.status === 'ImplementationApproved' || task.status === 'VerificationPlanning') {
    return <section className="verification-panel verification-generation">
      <p className="eyebrow">07 · MANUAL VERIFICATION</p>
      <h2>Generate manual verification plan</h2>
      <p className="verification-intro">Create bounded guidance for the exact approved revision. Forge will not execute commands or perform Git actions.</p>
      <div className="verification-status-strip" role="note">
        <strong>MANUAL — NOT EXECUTED BY FORGE</strong>
        <span>{capabilities?.verificationPlanningProvider ?? capabilities?.planningProvider ?? 'Unavailable'}</span>
        <span>{capabilities?.verificationPlanningModel ?? 'unavailable'}</span>
        <span>{capabilities?.verificationPlanningReasoningEffort ?? 'unavailable'} reasoning</span>
        <small>OpenAI API usage is billed separately when OpenAI mode is selected.</small>
      </div>
      <div className="verification-generation-action">
        <p role="status">{eligibilityMessage ?? 'Manual verification-plan generation is ready.'}</p>
        <button type="button" className="primary-button compact"
          disabled={busy || capabilities?.verificationPlanningConfigured === false || !canGenerate}
          onClick={onGenerate}>
          {canRetry ? 'Retry verification-plan generation' : isInitial ? 'Generate manual verification plan' : 'Verification generation unavailable'}
        </button>
      </div>
    </section>
  }

  if (!plan) return null

  const documentActions = <DocumentActions
    busy={documentsBusy || busy}
    error={documentError}
    onExportApprovedPlan={onExportApprovedPlan}
    onExportVerificationPlan={onExportPlan}
    onExportTaskReport={onExportTaskReport}
  />

  if (task.status === 'ReadyForDelivery') {
    return <section className="verification-panel verification-terminal ready-view">
      <p className="eyebrow">READY FOR DELIVERY</p>
      <h2>Manual verification passed — user reported</h2>
      <p className="verification-intro">The exact approved implementation revision received a bound plan and explicit human pass confirmation.</p>
      <VerificationMetadata rows={[
        ['Verification attempt', attempt?.attemptId ?? 'not recorded'],
        ['Plan', plan.planId],
        ['Implementation revision', plan.implementationRevisionId],
        ['Attempt fingerprint', attempt?.attemptFingerprint ?? 'not recorded'],
      ]} />
      <p className="verification-boundary" role="note">No automated validation, commit, push, or pull request was performed.</p>
      {documentActions}
    </section>
  }

  if (task.status === 'ManualVerificationFailed') {
    const failedResults = currentResults.filter(item => ['Failed', 'Blocked'].includes(item.result))
    return <section className="verification-panel verification-terminal verification-failed">
      <p className="eyebrow">MANUAL VERIFICATION FAILED · USER REPORTED</p>
      <h2>Failed or blocked outcomes were preserved</h2>
      <p className="verification-intro">These outcomes are user-reported and remain bound to the exact verification attempt.</p>
      <div className="verification-cases terminal-cases">
        {failedResults.map(result => {
          const testCase = plan.testCases.find(item => item.testCaseId === result.testCaseId)
          if (!testCase) return null
          const expanded = expandedCaseId === testCase.testCaseId
          return <article className={`verification-case-card status-${result.result.toLowerCase()}`} key={testCase.testCaseId}>
            <CaseSummaryButton testCase={testCase} result={result.result} expanded={expanded}
              onClick={() => setExpandedCaseId(expanded ? null : testCase.testCaseId)} />
            {expanded && <div className="verification-case-body" id={`verification-case-${testCase.testCaseId}`}>
              <ResultSummary result={result} />
              <CaseHistory attempt={attempt} testCase={testCase} />
            </div>}
          </article>
        })}
      </div>
      <p className="verification-boundary">Failure analysis and implementation correction are unavailable in this slice.</p>
      {documentActions}
    </section>
  }

  return <section className="verification-panel verification-workflow">
    <p className="eyebrow">07 · MANUAL VERIFICATION · USER REPORTED</p>
    <h2>Manual verification plan</h2>
    <p className="verification-summary">{plan.summary}</p>
    <p className="verification-scope">{plan.scope}</p>
    <div className="verification-status-strip" role="note"><strong>{plan.executionLabel}</strong><span>{plan.trustLabel}</span></div>
    <VerificationMetadata rows={[
      ['Implementation revision', plan.implementationRevisionId],
      ['Result fingerprint', plan.implementationResultFingerprint],
    ]} />
    {plan.preconditions.length > 0 && <details className="verification-preconditions">
      <summary>Preconditions <span>{plan.preconditions.length}</span></summary>
      <ul>{plan.preconditions.map(item => <li key={item}>{item}</li>)}</ul>
    </details>}
    <div className="verification-plan-actions">
      {!attempt && <button type="button" className="primary-button compact" disabled={!canStart}
        onClick={() => invokeOnce(canStart, onStart)}>Start verification</button>}
      {documentActions}
    </div>
    {attempt?.status === 'InProgress' && <>
      <p className="verification-progress" aria-live="polite"><strong>{completedRequired} of {requiredCount}</strong> required cases acceptable</p>
      <div className="verification-cases" aria-label="Verification cases">
        {plan.testCases.map(testCase => {
          const result = currentResults.find(item => item.testCaseId === testCase.testCaseId)
          const resultValue = result?.result ?? 'NotStarted'
          const expanded = expandedCaseId === testCase.testCaseId
          return <article className={`verification-case-card status-${resultValue.toLowerCase()}`} key={testCase.testCaseId}>
            <CaseSummaryButton testCase={testCase} result={resultValue} expanded={expanded}
              onClick={() => {
                setExpandedCaseId(expanded ? null : testCase.testCaseId)
                setDraft(null)
              }} />
            {expanded && <div className="verification-case-body" id={`verification-case-${testCase.testCaseId}`}>
              {result && <ResultSummary result={result} />}
              <form className="verification-case" onSubmit={event => {
                event.preventDefault()
                invokeOnce(canSave, () => onSaveCase(testCase, value))
              }}>
                <fieldset>
                  <legend>Record case {testCase.order}: {testCase.title}</legend>
                  <div className="verification-case-guidance">
                    <section><small>OBJECTIVE</small><p>{testCase.objective}</p></section>
                    <span className="manual-label">MANUAL — NOT EXECUTED BY FORGE</span>
                    <section><small>STEPS</small><ol>{testCase.orderedSteps.map(step => <li key={step.order}>
                      <p>{step.instruction}</p>
                      {step.approvedValidationCommandId && <code title={step.approvedValidationCommandId}>{step.approvedValidationCommandId}</code>}
                      <small>Expected observation: {step.expectedObservation}</small>
                    </li>)}</ol></section>
                    <section><small>EXPECTED RESULT</small><p>{testCase.expectedResult}</p></section>
                  </div>
                  <div className="verification-form-grid">
                    <label>Result<select value={value.result} onChange={event => setDraft({ ...value, result: event.target.value as VerificationCaseResult })}><option>NotStarted</option><option>Passed</option><option>Failed</option><option>Blocked</option><option>NotApplicable</option></select></label>
                    <label>Actual result<textarea rows={3} maxLength={2000} value={value.actualResult} onChange={event => setDraft({ ...value, actualResult: event.target.value })} /></label>
                    <label>Notes<textarea rows={3} maxLength={2000} value={value.notes} onChange={event => setDraft({ ...value, notes: event.target.value })} /></label>
                    <label>Evidence descriptions (one per line)<textarea rows={3} value={value.evidenceDescriptions.join('\n')} onChange={event => setDraft({ ...value, evidenceDescriptions: event.target.value.split('\n').filter(Boolean).slice(0, 6) })} /></label>
                    {value.result === 'NotApplicable' && <label>Reason required<textarea rows={3} required value={value.notApplicableReason} onChange={event => setDraft({ ...value, notApplicableReason: event.target.value })} /></label>}
                  </div>
                  {['Failed', 'Blocked'].includes(value.result) && <FailureFields value={value} setValue={setDraft} expected={testCase.expectedResult} />}
                  <div className="verification-save-row"><span>Results are appended to immutable history.</span><button className="primary-button compact" disabled={!canSave}>Save case result</button></div>
                </fieldset>
              </form>
              <CaseHistory attempt={attempt} testCase={testCase} />
            </div>}
          </article>
        })}
      </div>
      <fieldset className="verification-completion">
        <legend>Complete manual verification</legend>
        <p>Completion is explicit. Forge will not infer or automatically submit an outcome.</p>
        <label className="verification-confirmation"><input type="checkbox" checked={confirmed} onChange={event => setConfirmed(event.target.checked)} /><span>I confirm these outcomes were recorded by a human.</span></label>
        <label>Completion summary<textarea rows={3} maxLength={2000} value={summary} onChange={event => setSummary(event.target.value)} /></label>
        <div className="verification-completion-actions">
          <button type="button" className="primary-button compact" disabled={!canCompletePassed} onClick={() => {
            if (canCompletePassed && window.confirm('Complete this attempt as passed?')) invokeOnce(true, () => onComplete(true, summary, confirmed))
          }}>Complete as passed</button>
          <button type="button" className="secondary-button" disabled={!canCompleteFailed} onClick={() => {
            if (canCompleteFailed && window.confirm('Complete this attempt as failed?')) invokeOnce(true, () => onComplete(false, summary, confirmed))
          }}>Complete as failed</button>
        </div>
      </fieldset>
    </>}
  </section>
}

function CaseSummaryButton({ testCase, result, expanded, onClick }: {
  testCase: VerificationTestCase
  result: VerificationCaseResult
  expanded: boolean
  onClick: () => void
}) {
  return <button type="button" className="verification-case-summary" aria-expanded={expanded}
    aria-controls={`verification-case-${testCase.testCaseId}`} onClick={onClick}>
    <span className="case-number">{String(testCase.order).padStart(2, '0')}</span>
    <span className="case-title">{testCase.title}</span>
    <span className="case-requirement">{testCase.isRequired ? 'Required' : 'Optional'}</span>
    <span className="case-result"><span className="sr-only">Current result: </span>{result}</span>
    <span className="case-disclosure" aria-hidden="true">{expanded ? '−' : '+'}</span>
  </button>
}

function ResultSummary({ result }: { result: ManualCaseResultRevision }) {
  return <div className="verification-result-summary" role="status">
    <strong>{result.result} · USER REPORTED</strong>
    {result.actualResult && <p>{result.actualResult}</p>}
    {result.notes && <small>{result.notes}</small>}
    {result.failureDetails && <dl>
      <div><dt>Failure</dt><dd>{result.failureDetails.title}</dd></div>
      <div><dt>Actual result</dt><dd>{result.failureDetails.actualResult}</dd></div>
      <div><dt>Severity</dt><dd>{result.failureDetails.severity}</dd></div>
      {result.failureDetails.errorMessage && <div><dt>Error</dt><dd>{result.failureDetails.errorMessage}</dd></div>}
    </dl>}
  </div>
}

function CaseHistory({ attempt, testCase }: {
  attempt: ManualVerificationAttempt | null
  testCase: VerificationTestCase
}) {
  const revisions = attempt?.resultRevisions.filter(item => item.testCaseId === testCase.testCaseId) ?? []
  if (revisions.length === 0) return null
  return <details className="verification-case-history">
    <summary>Append-only result history <span>{revisions.length}</span></summary>
    {revisions.map(item => <article key={item.resultRevisionId}>
      <strong>Revision {item.revisionNumber} · {item.result}</strong>
      <span>USER REPORTED · {new Date(item.recordedAt).toLocaleString()}</span>
      {item.actualResult && <p>{item.actualResult}</p>}
    </article>)}
  </details>
}

function VerificationMetadata({ rows }: { rows: Array<[string, string]> }) {
  return <dl className="verification-metadata">{rows.map(([label, value]) => <div key={label}>
    <dt>{label}</dt><dd><code title={value}>{value}</code></dd>
  </div>)}</dl>
}

function DocumentActions({ busy, error, onExportApprovedPlan, onExportVerificationPlan, onExportTaskReport }: {
  busy: boolean
  error: string | null
  onExportApprovedPlan: () => void
  onExportVerificationPlan: () => void
  onExportTaskReport: () => void
}) {
  return <section className="verification-documents" aria-labelledby="verification-documents-title">
    <div><strong id="verification-documents-title">Documents</strong><p>Persisted, read-only workflow records.</p></div>
    <div className="verification-document-actions">
      <button type="button" className="secondary-button" disabled={busy} onClick={onExportApprovedPlan}>Approved plan PDF</button>
      <button type="button" className="secondary-button" disabled={busy} onClick={onExportVerificationPlan}>Verification plan PDF</button>
      <button type="button" className="secondary-button" disabled={busy} onClick={onExportTaskReport}>Task report PDF</button>
    </div>
    {error && <p className="export-error" role="alert">{error}</p>}
  </section>
}

function firstUnresolvedCaseId(plan: VerificationPlan | null, results: ManualCaseResultRevision[]) {
  if (!plan) return null
  const unresolved = (testCase: VerificationTestCase) =>
    !acceptableResults.has(results.find(item => item.testCaseId === testCase.testCaseId)?.result ?? 'NotStarted')
  return plan.testCases.find(item => item.isRequired && unresolved(item))?.testCaseId ??
    plan.testCases.find(item => !item.isRequired && unresolved(item))?.testCaseId ?? null
}

function nextUnresolvedCaseId(
  plan: VerificationPlan | null,
  results: ManualCaseResultRevision[],
  completedCaseId: string,
) {
  if (!plan) return null
  const currentOrder = plan.testCases.find(item => item.testCaseId === completedCaseId)?.order ?? 0
  const unresolved = plan.testCases.filter(item => item.testCaseId !== completedCaseId &&
    !acceptableResults.has(results.find(result => result.testCaseId === item.testCaseId)?.result ?? 'NotStarted'))
  const ordered = (required: boolean) => unresolved.filter(item => item.isRequired === required)
    .sort((left, right) => Number(left.order <= currentOrder) - Number(right.order <= currentOrder) || left.order - right.order)
  return ordered(true)[0]?.testCaseId ?? ordered(false)[0]?.testCaseId ?? null
}

function FailureFields({ value, setValue, expected }: {
  value: CaseFormValue
  setValue: (value: CaseFormValue) => void
  expected: string
}) {
  const failure = value.failureDetails ?? {
    title: '', expectedResult: expected, actualResult: value.actualResult, reproductionSteps: [],
    environmentNotes: [], errorMessage: null, evidenceDescriptions: value.evidenceDescriptions,
    severity: 'Medium' as const,
  }
  const update = (patch: Partial<VerificationFailureDetails>) =>
    setValue({ ...value, failureDetails: { ...failure, ...patch } })
  return <fieldset className="verification-failure-fields">
    <legend>Failure details required</legend>
    <label>Title<input required maxLength={160} value={failure.title} onChange={event => update({ title: event.target.value })} /></label>
    <label>Actual result<textarea rows={3} required maxLength={2000} value={failure.actualResult} onChange={event => update({ actualResult: event.target.value })} /></label>
    <label>Reproduction steps (one per line)<textarea rows={3} required value={failure.reproductionSteps.join('\n')} onChange={event => update({ reproductionSteps: event.target.value.split('\n').filter(Boolean).slice(0, 12) })} /></label>
    <label>Environment notes (one per line)<textarea rows={3} value={failure.environmentNotes.join('\n')} onChange={event => update({ environmentNotes: event.target.value.split('\n').filter(Boolean).slice(0, 8) })} /></label>
    <label>Error message<textarea rows={3} maxLength={2000} value={failure.errorMessage ?? ''} onChange={event => update({ errorMessage: event.target.value || null })} /></label>
    <label>Severity<select value={failure.severity} onChange={event => update({ severity: event.target.value as VerificationFailureDetails['severity'] })}><option>Low</option><option>Medium</option><option>High</option><option>Critical</option></select></label>
  </fieldset>
}
