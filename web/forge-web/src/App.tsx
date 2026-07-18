import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { ForgeApiError, forgeApi } from './api'
import type { EngineeringTask, SystemCapabilities, WorkflowStatus } from './types'
import './App.css'

const stages = ['Understand', 'Clarify', 'Confirm', 'Plan', 'Implement', 'Validate', 'Review', 'Pull Request']
const stageByStatus: Record<WorkflowStatus, number> = { Draft: 0, Clarifying: 1, RequirementSummaryReady: 2, AwaitingRequirementApproval: 2, ReadyForPlanning: 3, Planning: 3, AwaitingPlanApproval: 3, PlanApproved: 3, Implementing: 4, Validating: 5, Reviewing: 6, Completed: 7, Failed: 0 }

function App() {
  const [repository, setRepository] = useState('')
  const [requirement, setRequirement] = useState('')
  const [answer, setAnswer] = useState('')
  const [correction, setCorrection] = useState('')
  const [correctionMode, setCorrectionMode] = useState(false)
  const [task, setTask] = useState<EngineeringTask | null>(null)
  const [capabilities, setCapabilities] = useState<SystemCapabilities | null>(null)
  const [capabilitiesUnavailable, setCapabilitiesUnavailable] = useState(false)
  const [busy, setBusy] = useState(false)
  const [planningProgress, setPlanningProgress] = useState<number | null>(null)
  const [planningFailure, setPlanningFailure] = useState<string | null>(null)
  const [planningSnapshotStale, setPlanningSnapshotStale] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const activeStage = task ? stageByStatus[task.status] : 0
  const answeredCount = task?.clarificationAnswers.length ?? 0
  const taskLabel = useMemo(() => task ? `FORGE-${task.id.slice(0, 6).toUpperCase()}` : 'NEW TASK', [task])

  useEffect(() => {
    forgeApi.getCapabilities()
      .then(setCapabilities)
      .catch(() => setCapabilitiesUnavailable(true))
  }, [])

  async function run(action: () => Promise<EngineeringTask>) {
    setBusy(true); setError(null)
    try { setTask(await action()) }
    catch (caught) { setError(caught instanceof Error ? caught.message : 'An unexpected error occurred.') }
    finally { setBusy(false) }
  }

  function createTask(event: FormEvent) { event.preventDefault(); void run(() => forgeApi.createTask(repository, requirement)) }
  function submitAnswer(event: FormEvent) {
    event.preventDefault(); if (!task) return
    const submitted = answer
    void run(async () => { const updated = await forgeApi.answerQuestion(task.id, submitted); setAnswer(''); return updated })
  }
  function submitCorrection(event: FormEvent) {
    event.preventDefault(); if (!task) return
    const submitted = correction
    void run(async () => {
      const updated = await forgeApi.requestRevision(task.id, submitted)
      setCorrection(''); setCorrectionMode(false)
      return updated
    })
  }
  function approveSummary() { if (task) void run(() => forgeApi.approveRequirement(task.id)) }
  async function analyzeAndPlan() {
    if (!task) return
    let repositoryAnalyzed = false
    setBusy(true); setError(null); setPlanningFailure(null); setPlanningSnapshotStale(false); setPlanningProgress(0)
    try {
      const analyzed = await forgeApi.analyzeRepository(task.id)
      repositoryAnalyzed = true; setTask(analyzed); setPlanningProgress(2)
      const planned = await forgeApi.createPlan(task.id)
      setTask(planned); setPlanningProgress(3)
    } catch (caught) {
      const message = caught instanceof Error ? caught.message : 'Planning could not be completed.'
      if (repositoryAnalyzed) {
        try { setTask(await forgeApi.getTask(task.id)) } catch { /* Keep the analyzed task if refresh fails. */ }
        setPlanningSnapshotStale(caught instanceof ForgeApiError && caught.code === 'stale_snapshot')
        setPlanningFailure(message)
      }
      else setError(message)
    }
    finally { setBusy(false) }
  }
  async function retryPlanGeneration() {
    if (!task || task.status !== 'Planning') return
    setBusy(true); setError(null); setPlanningFailure(null); setPlanningSnapshotStale(false); setPlanningProgress(2)
    try {
      const planned = await forgeApi.createPlan(task.id)
      setTask(planned); setPlanningProgress(3)
    } catch (caught) {
      try { setTask(await forgeApi.getTask(task.id)) } catch { /* Keep the current task if refresh fails. */ }
      setPlanningSnapshotStale(caught instanceof ForgeApiError && caught.code === 'stale_snapshot')
      setPlanningFailure(caught instanceof Error ? caught.message : 'Planning could not be completed.')
    } finally { setBusy(false) }
  }
  function approvePlan() { if (task) void run(() => forgeApi.approvePlan(task.id)) }
  function startAnother() { setTask(null); setRepository(''); setRequirement(''); setAnswer(''); setCorrection(''); setCorrectionMode(false); setPlanningFailure(null); setPlanningSnapshotStale(false); setError(null) }

  const telemetry = task?.telemetry ?? { totalCalls: 0, totalInputTokens: 0, totalCachedInputTokens: 0, totalOutputTokens: 0, totalEstimatedCostUsd: 0, calls: [] }
  const clarificationCalls = telemetry.calls.filter(call => call.stage === 'Clarification')
  const planningCalls = telemetry.calls.filter(call => call.stage === 'Planning')
  const planningCost = planningCalls.reduce((total, call) => total + call.estimatedCostUsd, 0)
  const latestFailedPlanningCall = [...planningCalls].reverse().find(call => !call.succeeded)
  const planningFailureMessage = planningFailure ?? (task?.status === 'Planning' && latestFailedPlanningCall
    ? planningFailureForCategory(latestFailedPlanningCall.failureCategory)
    : null)
  const providerLabel = capabilitiesUnavailable
    ? 'Provider status unavailable'
    : capabilities?.aiMode === 'Fake'
      ? 'Deterministic demo adapter · no AI calls'
      : capabilities?.aiMode === 'OpenAI'
        ? capabilities.aiConfigured ? `OpenAI · clarify ${capabilities.clarificationModel} · plan ${capabilities.planningModel}` : 'OpenAI configuration required'
        : 'Checking provider configuration…'

  return <div className="app-shell">
    <header className="topbar">
      <a className="brand" href="/" aria-label="Forge AI home"><span className="brand-mark" aria-hidden="true"><span /></span><span>Forge <b>AI</b></span></a>
      <div className="topbar-meta"><span className={`environment ${capabilities?.aiConfigured === false ? 'warning' : ''}`}><i />{capabilities?.aiMode ?? 'Checking mode'}</span><span className="divider" /><span className="task-label">{taskLabel}</span></div>
    </header>
    <main>
      <section className="hero-copy"><p className="eyebrow">REQUIREMENT → REVIEWED CHANGE</p><h1>Build software with<br /><em>evidence, not guesses.</em></h1><p className="subtitle">A trustworthy, explainable and cost-aware engineering agent that turns intent into a reviewed pull request.</p></section>
      <nav className="progress" aria-label="Workflow progress">{stages.map((stage, index) => {
        const state = task?.status === 'PlanApproved'
          ? index <= 3 ? 'complete' : 'future'
          : index < activeStage ? 'complete' : index === activeStage ? 'active' : 'future'
        return <div className={`progress-step ${state}`} key={stage}><span className="step-node">{state === 'complete' ? '✓' : String(index + 1).padStart(2, '0')}</span><span className="step-name">{stage}</span>{index < stages.length - 1 && <span className="step-line" />}</div>
      })}</nav>
      {error && <div className="alert" role="alert"><span>!</span><div><strong>We couldn’t complete that action.</strong><p>{error}</p></div><button onClick={() => setError(null)} aria-label="Dismiss error">×</button></div>}
      <div className="workspace">
        <section className="action-card" aria-busy={busy}>
          <div className="card-heading"><div><span className="section-number">{task ? String(activeStage + 1).padStart(2, '0') : '01'}</span><p>{task ? stages[activeStage].toUpperCase() : 'UNDERSTAND'}</p></div>{task && <span className="status-pill"><i />{formatStatus(task.status)}</span>}</div>
          {!task && <form className="task-form" onSubmit={createTask}>
            <div className="action-title"><span className="title-icon">⌁</span><div><h2>What are we building?</h2><p>Point Forge at a local repository and describe the outcome you need.</p></div></div>
            <label><span>LOCAL REPOSITORY PATH</span><div className="input-frame"><span aria-hidden="true">⌘</span><input value={repository} onChange={event => setRepository(event.target.value)} placeholder="C:\Projects\your-repository" required /></div></label>
            <label><span>REQUIREMENT OR WORK ITEM</span><textarea value={requirement} onChange={event => setRequirement(event.target.value)} placeholder="Describe the change, why it matters, and any known constraints…" rows={7} maxLength={10000} required /><small>{requirement.length.toLocaleString()} / 10,000</small></label>
            <button className="primary-button" disabled={busy || !repository.trim() || !requirement.trim()}>{busy ? <><span className="spinner" />Evaluating…</> : <>Analyze requirement <span>→</span></>}</button>
            <p className="form-note"><span>i</span> Repository inspection occurs read-only only after requirement approval.</p>
          </form>}
          {task?.status === 'Clarifying' && task.currentPendingQuestion && <form className="question-form" onSubmit={submitAnswer}>
            <div className="question-count"><span>QUESTION {answeredCount + 1}</span><span className="single-question-note">ONE AT A TIME</span></div>
            <div className="action-title"><span className="title-icon">?</span><div><h2>One detail before we continue</h2><p>Only the highest-value unresolved question is shown.</p></div></div>
            <blockquote>{task.currentPendingQuestion}</blockquote>
            <label><span>YOUR ANSWER</span><textarea autoFocus value={answer} onChange={event => setAnswer(event.target.value)} placeholder="Be as specific as you can…" rows={5} maxLength={5000} required /></label>
            <button className="primary-button" disabled={busy || !answer.trim()}>{busy ? <><span className="spinner" />Reevaluating…</> : <>Save & reevaluate <span>→</span></>}</button>
            <p className="form-note"><span>i</span> Earlier answers and correction notes remain part of the canonical context.</p>
          </form>}
          {task?.status === 'AwaitingRequirementApproval' && task.requirementSummary && <div className="summary-view">
            <div className="action-title"><span className="title-icon">✓</span><div><h2>Confirm the requirement</h2><p>Approve the current summary or request one focused correction.</p></div></div>
            <div className="summary-paper"><span className="paper-label">CURRENT REQUIREMENT SUMMARY</span><pre>{task.requirementSummary}</pre></div>
            {!correctionMode ? <div className="approval-row"><p><strong>Explicit approval required</strong><br />Approval locks this summary as planning context.</p><div className="approval-actions"><button className="secondary-button" onClick={() => setCorrectionMode(true)}>Request correction</button><button className="primary-button compact" onClick={approveSummary} disabled={busy}>Approve requirement <span>→</span></button></div></div>
              : <form className="correction-form" onSubmit={submitCorrection}><div><strong>Request a correction</strong><p>The current summary will be preserved in revision history and regenerated.</p></div><label><span>CORRECTION NOTE</span><textarea autoFocus value={correction} onChange={event => setCorrection(event.target.value)} placeholder="State only what should change…" rows={4} maxLength={5000} required /></label><div className="approval-actions"><button type="button" className="secondary-button" onClick={() => { setCorrectionMode(false); setCorrection('') }}>Cancel</button><button className="primary-button compact" disabled={busy || !correction.trim()}>{busy ? 'Regenerating…' : 'Submit correction'}</button></div></form>}
          </div>}
          {task?.status === 'ReadyForPlanning' && <div className="ready-view"><div className="success-seal">✓</div><p className="eyebrow">REQUIREMENT APPROVED</p><h2>Ready for evidence-backed planning</h2><p>Forge will inspect the repository read-only, select bounded evidence, and use the configured planning provider.</p><div className="coming-next"><span>04</span><div><strong>Read-only planning</strong><p>No files, Git state, packages, builds, or tests will be changed.</p></div><button className="primary-button compact" onClick={() => void analyzeAndPlan()} disabled={busy}>Analyze repository and create plan</button></div><button className="text-button" onClick={startAnother}>Start another task</button></div>}
          {task?.status === 'Planning' && planningFailureMessage && !busy && <div className="planning-failure"><div className="failure-seal">!</div><p className="eyebrow">PLAN GENERATION FAILED</p><h2>{planningSnapshotStale ? 'The repository needs a fresh read-only analysis.' : 'The existing evidence is ready for another attempt.'}</h2><p>{planningFailureMessage}</p>{latestFailedPlanningCall && <div className="failed-call-summary"><strong>{latestFailedPlanningCall.model} · {latestFailedPlanningCall.failureCategory}</strong><span>{latestFailedPlanningCall.outputTokens.toLocaleString()} output tokens · estimated ${latestFailedPlanningCall.estimatedCostUsd.toFixed(6)}</span></div>}{planningSnapshotStale ? <button className="primary-button compact" onClick={() => void analyzeAndPlan()} disabled={busy}>Re-analyze repository and create plan <span>→</span></button> : <button className="primary-button compact" onClick={() => void retryPlanGeneration()} disabled={busy}>Retry plan generation <span>→</span></button>}<small>{planningSnapshotStale ? 'The changed repository invalidated the saved snapshot; analysis must be refreshed first.' : 'Retry uses this snapshot and selected evidence. Repository analysis is not repeated.'}</small></div>}
          {((task?.status === 'Planning' && !planningFailureMessage) || planningProgress !== null && busy) && <div className="planning-progress"><div className="action-title"><span className="title-icon">04</span><div><h2>Creating an evidence-backed plan</h2><p>The target remains read-only throughout analysis.</p></div></div>{['Inspecting repository', 'Selecting evidence', 'Creating evidence-backed plan', 'Awaiting approval'].map((label, index) => <div className={`planning-progress-step ${index < (planningProgress ?? 0) ? 'complete' : index === (planningProgress ?? 0) ? 'active' : ''}`} key={label}><span>{index < (planningProgress ?? 0) ? '✓' : index + 1}</span><p>{label}</p></div>)}</div>}
          {task?.status === 'AwaitingPlanApproval' && task.implementationPlan && <div className="plan-view">
            <div className="action-title"><span className="title-icon">✓</span><div><h2>Review the evidence-backed plan</h2><p>Every existing file proposal is linked to selected repository evidence.</p></div></div>
            <span className="fake-label">{task.implementationPlan.source === 'OpenAI' ? `OpenAI plan · ${task.implementationPlan.planningModel}` : 'Deterministic Fake plan'} · NO CODE CHANGED</span>
            <section className="plan-section"><small>OBJECTIVE</small><p>{task.implementationPlan.objective}</p></section>
            <section className="plan-section"><small>REPOSITORY UNDERSTANDING</small><p>{task.implementationPlan.repositoryUnderstanding}</p></section>
            <section className="plan-section"><small>AFFECTED FILES</small>{task.implementationPlan.affectedFiles.map(file => <article className="planned-file" key={`${file.action}-${file.path}`}><div><code>{file.path}</code><span>{file.action}</span></div><p>{file.purpose}</p><small>Evidence: {file.evidenceIds.join(', ') || 'none'} · confidence {Math.round(file.confidence * 100)}%</small></article>)}</section>
            <section className="plan-section"><small>ORDERED STEPS</small><ol>{task.implementationPlan.orderedSteps.map(step => <li key={step.order}><strong>{step.description}</strong><p>{step.expectedResult}</p><small>Paths: {step.affectedPaths.join(', ')} · Evidence: {step.evidenceIds.join(', ') || 'none'}</small></li>)}</ol></section>
            <section className="plan-section"><small>PROPOSED VALIDATION · NOT RUN</small>{task.implementationPlan.proposedValidationCommands.map(command => <code className="validation-command" key={command}>{command}</code>)}</section>
            <div className="plan-columns"><section className="plan-section"><small>RISKS</small><ul>{task.implementationPlan.risks.map(risk => <li key={risk}>{risk}</li>)}</ul></section><section className="plan-section"><small>ASSUMPTIONS</small><ul>{task.implementationPlan.assumptions.map(assumption => <li key={assumption}>{assumption}</li>)}</ul></section></div>
            {task.implementationPlan.unresolvedQuestions.length > 0 && <section className="plan-section"><small>UNRESOLVED QUESTIONS</small><ul>{task.implementationPlan.unresolvedQuestions.map(question => <li key={question}>{question}</li>)}</ul></section>}
            <div className="approval-row"><p><strong>Explicit plan approval required</strong><br />Approval records the gate; implementation remains unavailable.</p><div className="approval-actions"><button className="secondary-button" onClick={() => void analyzeAndPlan()} disabled={busy}>Re-analyze repository</button><button className="primary-button compact" onClick={approvePlan} disabled={busy}>Approve plan <span>→</span></button></div></div>
          </div>}
          {task?.status === 'PlanApproved' && <div className="ready-view"><div className="success-seal">✓</div><p className="eyebrow">PLAN APPROVED</p><h2>Plan approved. Implementation is the next milestone.</h2><p>The approved evidence and plan remain persisted. Forge did not modify the target repository.</p><button className="text-button" onClick={startAnother}>Start another task</button></div>}
          {task && task.evidenceItems.length > 0 && <section className="evidence-view"><div className="evidence-heading"><div><p className="eyebrow">SELECTED REPOSITORY EVIDENCE</p><h3>{task.evidenceFilesSelected} files · {task.totalEvidenceCharacters.toLocaleString()} characters</h3></div><span>{task.evidenceFilesInspected} eligible files inspected</span></div>{task.evidenceItems.map(item => <details className="evidence-item" key={item.id}><summary><b>{item.id}</b><code>{item.relativePath}:{item.startLine}-{item.endLine}</code><span>score {item.score}</span></summary><p>{item.reasonSelected}</p><pre>{item.excerpt}</pre></details>)}</section>}
        </section>
        <aside className="context-column">
          <section className="context-card"><div className="aside-title"><span>Repository context</span><i>{task?.repositorySnapshot ? 'READ-ONLY SNAPSHOT' : task ? 'IDENTIFIER ONLY' : 'WAITING'}</i></div>{task ? <><code>{task.repository}</code>{task.repositorySnapshot ? <div className="repository-map"><dl><div><dt>Branch</dt><dd>{task.repositorySnapshot.branch ?? 'n/a'}</dd></div><div><dt>HEAD</dt><dd>{task.repositorySnapshot.shortHeadSha ?? 'n/a'}</dd></div><div><dt>State</dt><dd>{task.repositorySnapshot.workingTreeStatus}</dd></div><div><dt>Files</dt><dd>{task.repositorySnapshot.totalDiscoveredFiles}</dd></div><div><dt>Eligible</dt><dd>{task.repositorySnapshot.eligibleTextFileCount}</dd></div><div><dt>Excluded</dt><dd>{task.repositorySnapshot.excludedFileCount}</dd></div></dl><p><strong>Detected stack</strong><br />{task.repositorySnapshot.detectedLanguages.join(' · ') || 'No supported stack detected'}</p><p><strong>Projects/packages</strong><br />{task.repositorySnapshot.projectFiles.join(', ') || 'None detected'}</p><p><strong>Snapshot freshness</strong><br />{new Date(task.repositorySnapshot.analyzedAt).toLocaleString()}</p>{task.repositorySnapshot.warnings.map(warning => <p className="snapshot-warning" key={warning}>! {warning}</p>)}</div> : <p className="truth-note"><span>!</span> Files are not inspected until requirement approval.</p>}</> : <div className="empty-context"><span>⌘</span><p>Repository details appear after task creation.</p></div>}</section>
          <section className="context-card history-card"><div className="aside-title"><span>Clarification history</span><b>{answeredCount}</b></div>{task && answeredCount > 0 ? task.clarificationAnswers.map((item, index) => <details key={item.answeredAt} open={index === answeredCount - 1}><summary><span>{String(index + 1).padStart(2, '0')}</span>{item.question}</summary><p>{item.answer}</p></details>) : <div className="empty-context compact"><p>Answers are preserved here.</p></div>}</section>
          {task && task.requirementRevisionNotes.length > 0 && <section className="context-card history-card"><div className="aside-title"><span>Summary revisions</span><b>{task.requirementRevisionNotes.length}</b></div>{task.requirementRevisionNotes.map((revision, index) => <details key={revision.submittedAt}><summary><span>R{index + 1}</span>{revision.correction}</summary><p><strong>Previous summary</strong><br />{revision.previousSummary}</p></details>)}</section>}
          <section className="telemetry-card expanded"><div><span>CLARIFICATION CALLS</span><strong>{clarificationCalls.length}</strong></div><div><span>PLANNING CALLS</span><strong>{planningCalls.length}</strong></div><div><span>INPUT TOKENS</span><strong>{formatTokens(telemetry.totalInputTokens)}</strong></div><div><span>OUTPUT TOKENS</span><strong>{formatTokens(telemetry.totalOutputTokens)}</strong></div><div><span>PLANNING EST. COST</span><strong>${planningCost.toFixed(6)}</strong></div><div><span>TOTAL EST. COST</span><strong>${telemetry.totalEstimatedCostUsd.toFixed(6)}</strong></div><p><i />{providerLabel}</p>{telemetry.calls.length > 0 && <details className="call-history"><summary>Model call details</summary>{telemetry.calls.map(call => <article key={call.id}><strong>{call.stage} · {call.model}</strong><span className={call.succeeded ? 'call-success' : 'call-failure'}>{call.succeeded ? 'Succeeded' : 'Failed'}</span><small>Reasoning: {call.reasoningEffort} · In {call.inputTokens} ({call.cachedInputTokens} cached) · Out {call.outputTokens} · Est. ${call.estimatedCostUsd.toFixed(6)}</small></article>)}</details>}</section>
        </aside>
      </div>
    </main>
    <footer><span>FORGE AI · BUILD WEEK EDITION</span><p><i />{providerLabel}<b>·</b>Local SQLite persistence</p></footer>
  </div>
}

function formatStatus(status: WorkflowStatus) { return status.replace(/([a-z])([A-Z])/g, '$1 $2') }
function formatTokens(tokens: number) { return tokens.toLocaleString() }
function planningFailureForCategory(category: string | null) {
  if (category === 'output_truncated') return 'The planning response reached its output limit before the structured plan was complete.'
  if (category === 'content_filter') return "The planning response was stopped by the provider's content filter."
  return 'OpenAI did not return a valid completed planning response.'
}
export default App
