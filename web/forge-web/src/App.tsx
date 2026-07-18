import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { forgeApi } from './api'
import type { EngineeringTask, SystemCapabilities, WorkflowStatus } from './types'
import './App.css'

const stages = ['Understand', 'Clarify', 'Confirm', 'Plan', 'Implement', 'Validate', 'Review', 'Pull Request']
const stageByStatus: Record<WorkflowStatus, number> = { Draft: 0, Clarifying: 1, RequirementSummaryReady: 2, AwaitingRequirementApproval: 2, ReadyForPlanning: 3, Planning: 3, AwaitingPlanApproval: 3, Implementing: 4, Validating: 5, Reviewing: 6, Completed: 7, Failed: 0 }

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
  function startAnother() { setTask(null); setRepository(''); setRequirement(''); setAnswer(''); setCorrection(''); setCorrectionMode(false); setError(null) }

  const telemetry = task?.telemetry ?? { totalCalls: 0, totalInputTokens: 0, totalCachedInputTokens: 0, totalOutputTokens: 0, totalEstimatedCostUsd: 0, calls: [] }
  const providerLabel = capabilitiesUnavailable
    ? 'Provider status unavailable'
    : capabilities?.aiMode === 'Fake'
      ? 'Deterministic demo adapter · no AI calls'
      : capabilities?.aiMode === 'OpenAI'
        ? capabilities.aiConfigured ? `OpenAI · ${capabilities.clarificationModel}` : 'OpenAI configuration required'
        : 'Checking provider configuration…'

  return <div className="app-shell">
    <header className="topbar">
      <a className="brand" href="/" aria-label="Forge AI home"><span className="brand-mark" aria-hidden="true"><span /></span><span>Forge <b>AI</b></span></a>
      <div className="topbar-meta"><span className={`environment ${capabilities?.aiConfigured === false ? 'warning' : ''}`}><i />{capabilities?.aiMode ?? 'Checking mode'}</span><span className="divider" /><span className="task-label">{taskLabel}</span></div>
    </header>
    <main>
      <section className="hero-copy"><p className="eyebrow">REQUIREMENT → REVIEWED CHANGE</p><h1>Build software with<br /><em>evidence, not guesses.</em></h1><p className="subtitle">A trustworthy, explainable and cost-aware engineering agent that turns intent into a reviewed pull request.</p></section>
      <nav className="progress" aria-label="Workflow progress">{stages.map((stage, index) => {
        const state = index < activeStage ? 'complete' : index === activeStage ? 'active' : 'future'
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
            <button className="primary-button" disabled={busy || !repository.trim() || !requirement.trim()}>{busy ? <><span className="spinner" />Evaluating…</> : <>Start clarification <span>→</span></>}</button>
            <p className="form-note"><span>i</span> No repository files will be inspected or modified in this slice.</p>
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
          {task?.status === 'ReadyForPlanning' && <div className="ready-view"><div className="success-seal">✓</div><p className="eyebrow">REQUIREMENT APPROVED</p><h2>Ready for planning</h2><p>The approved context is preserved. Repository-aware planning remains a future milestone.</p><div className="coming-next"><span>04</span><div><strong>Planning</strong><p>Coming next · not implemented in this slice</p></div><button disabled>Not available yet</button></div><button className="text-button" onClick={startAnother}>Start another task</button></div>}
        </section>
        <aside className="context-column">
          <section className="context-card"><div className="aside-title"><span>Repository context</span><i>{task ? 'IDENTIFIER ONLY' : 'WAITING'}</i></div>{task ? <><code>{task.repository}</code><p className="truth-note"><span>!</span> Repository inspection is not implemented.</p></> : <div className="empty-context"><span>⌘</span><p>Repository details appear after task creation.</p></div>}</section>
          <section className="context-card history-card"><div className="aside-title"><span>Clarification history</span><b>{answeredCount}</b></div>{task && answeredCount > 0 ? task.clarificationAnswers.map((item, index) => <details key={item.answeredAt} open={index === answeredCount - 1}><summary><span>{String(index + 1).padStart(2, '0')}</span>{item.question}</summary><p>{item.answer}</p></details>) : <div className="empty-context compact"><p>Answers are preserved here.</p></div>}</section>
          {task && task.requirementRevisionNotes.length > 0 && <section className="context-card history-card"><div className="aside-title"><span>Summary revisions</span><b>{task.requirementRevisionNotes.length}</b></div>{task.requirementRevisionNotes.map((revision, index) => <details key={revision.submittedAt}><summary><span>R{index + 1}</span>{revision.correction}</summary><p><strong>Previous summary</strong><br />{revision.previousSummary}</p></details>)}</section>}
          <section className="telemetry-card expanded"><div><span>MODEL CALLS</span><strong>{telemetry.totalCalls}</strong></div><div><span>INPUT</span><strong>{formatTokens(telemetry.totalInputTokens)}</strong></div><div><span>CACHED INPUT</span><strong>{formatTokens(telemetry.totalCachedInputTokens)}</strong></div><div><span>OUTPUT</span><strong>{formatTokens(telemetry.totalOutputTokens)}</strong></div><div><span>EST. COST</span><strong>${telemetry.totalEstimatedCostUsd.toFixed(6)}</strong></div><p><i />{providerLabel}</p>{telemetry.calls.length > 0 && <details className="call-history"><summary>Model call details</summary>{telemetry.calls.map(call => <article key={call.id}><strong>{call.stage} · {call.model}</strong><span className={call.succeeded ? 'call-success' : 'call-failure'}>{call.succeeded ? 'Succeeded' : 'Failed'}</span><small>Reasoning: {call.reasoningEffort} · In {call.inputTokens} ({call.cachedInputTokens} cached) · Out {call.outputTokens} · Est. ${call.estimatedCostUsd.toFixed(6)}</small></article>)}</details>}</section>
        </aside>
      </div>
    </main>
    <footer><span>FORGE AI · BUILD WEEK EDITION</span><p><i />{providerLabel}<b>·</b>Local SQLite persistence</p></footer>
  </div>
}

function formatStatus(status: WorkflowStatus) { return status.replace(/([a-z])([A-Z])/g, '$1 $2') }
function formatTokens(tokens: number) { return tokens.toLocaleString() }
export default App
