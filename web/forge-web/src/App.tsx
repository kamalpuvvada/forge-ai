import { useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { forgeApi } from './api'
import type { EngineeringTask, WorkflowStatus } from './types'
import './App.css'

const stages = ['Understand', 'Clarify', 'Confirm', 'Plan', 'Implement', 'Validate', 'Review', 'Pull Request']
const stageByStatus: Record<WorkflowStatus, number> = { Draft: 0, Clarifying: 1, RequirementSummaryReady: 2, AwaitingRequirementApproval: 2, ReadyForPlanning: 3, Planning: 3, AwaitingPlanApproval: 3, Implementing: 4, Validating: 5, Reviewing: 6, Completed: 7, Failed: 0 }

function App() {
  const [repository, setRepository] = useState('')
  const [requirement, setRequirement] = useState('')
  const [answer, setAnswer] = useState('')
  const [task, setTask] = useState<EngineeringTask | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const activeStage = task ? stageByStatus[task.status] : 0
  const answeredCount = task?.clarificationAnswers.length ?? 0
  const taskLabel = useMemo(() => task ? `FORGE-${task.id.slice(0, 6).toUpperCase()}` : 'NEW TASK', [task])

  async function run(action: () => Promise<EngineeringTask>) {
    setBusy(true); setError(null)
    try { setTask(await action()) }
    catch (caught) { setError(caught instanceof Error ? caught.message : 'An unexpected error occurred.') }
    finally { setBusy(false) }
  }

  function createTask(event: FormEvent) { event.preventDefault(); void run(() => forgeApi.createTask(repository, requirement)) }
  function submitAnswer(event: FormEvent) {
    event.preventDefault(); if (!task) return
    const submittedAnswer = answer
    void run(async () => { const updated = await forgeApi.answerQuestion(task.id, submittedAnswer); setAnswer(''); return updated })
  }
  function approveSummary() { if (task) void run(() => forgeApi.approveRequirement(task.id)) }
  function startAnother() { setTask(null); setRepository(''); setRequirement(''); setAnswer(''); setError(null) }

  return (
    <div className="app-shell">
      <header className="topbar">
        <a className="brand" href="/" aria-label="Forge AI home"><span className="brand-mark" aria-hidden="true"><span /></span><span>Forge <b>AI</b></span></a>
        <div className="topbar-meta"><span className="environment"><i /> Development mode</span><span className="divider" /><span className="task-label">{taskLabel}</span></div>
      </header>
      <main>
        <section className="hero-copy"><p className="eyebrow">REQUIREMENT → REVIEWED CHANGE</p><h1>Build software with<br /><em>evidence, not guesses.</em></h1><p className="subtitle">A trustworthy, explainable and cost-aware engineering agent that turns intent into a reviewed pull request.</p></section>
        <nav className="progress" aria-label="Workflow progress">
          {stages.map((stage, index) => {
            const state = index < activeStage ? 'complete' : index === activeStage ? 'active' : 'future'
            return <div className={`progress-step ${state}`} key={stage}><span className="step-node">{state === 'complete' ? '✓' : String(index + 1).padStart(2, '0')}</span><span className="step-name">{stage}</span>{index < stages.length - 1 && <span className="step-line" />}</div>
          })}
        </nav>
        {error && <div className="alert" role="alert"><span>!</span><div><strong>We couldn’t complete that action.</strong><p>{error}</p></div><button onClick={() => setError(null)} aria-label="Dismiss error">×</button></div>}
        <div className="workspace">
          <section className="action-card">
            <div className="card-heading"><div><span className="section-number">{task ? String(activeStage + 1).padStart(2, '0') : '01'}</span><p>{task ? stages[activeStage].toUpperCase() : 'UNDERSTAND'}</p></div>{task && <span className="status-pill"><i />{formatStatus(task.status)}</span>}</div>
            {!task && <form className="task-form" onSubmit={createTask}>
              <div className="action-title"><span className="title-icon">⌁</span><div><h2>What are we building?</h2><p>Point Forge at a local repository and describe the outcome you need.</p></div></div>
              <label><span>LOCAL REPOSITORY PATH</span><div className="input-frame"><span aria-hidden="true">⌘</span><input value={repository} onChange={e => setRepository(e.target.value)} placeholder="C:\Projects\your-repository" required /></div></label>
              <label><span>REQUIREMENT OR WORK ITEM</span><textarea value={requirement} onChange={e => setRequirement(e.target.value)} placeholder="Describe the change, why it matters, and any known constraints…" rows={7} maxLength={10000} required /><small>{requirement.length.toLocaleString()} / 10,000</small></label>
              <button className="primary-button" disabled={busy || !repository.trim() || !requirement.trim()}>{busy ? <><span className="spinner" />Creating task…</> : <>Start analysis <span>→</span></>}</button>
              <p className="form-note"><span>i</span> No files will be modified. This slice only clarifies and confirms requirements.</p>
            </form>}
            {task?.status === 'Clarifying' && task.currentPendingQuestion && <form className="question-form" onSubmit={submitAnswer}>
              <div className="question-count"><span>QUESTION {answeredCount + 1} OF 3</span><div><i style={{ width: `${((answeredCount + 1) / 3) * 100}%` }} /></div></div>
              <div className="action-title"><span className="title-icon">?</span><div><h2>One detail before we continue</h2><p>Only the highest-priority open question is shown.</p></div></div>
              <blockquote>{task.currentPendingQuestion}</blockquote>
              <label><span>YOUR ANSWER</span><textarea autoFocus value={answer} onChange={e => setAnswer(e.target.value)} placeholder="Be as specific as you can…" rows={5} maxLength={5000} required /></label>
              <button className="primary-button" disabled={busy || !answer.trim()}>{busy ? <><span className="spinner" />Saving answer…</> : <>Save & continue <span>→</span></>}</button>
              <p className="form-note"><span>i</span> Your earlier answers are preserved and used in the summary.</p>
            </form>}
            {task?.status === 'AwaitingRequirementApproval' && task.requirementSummary && <div className="summary-view">
              <div className="action-title"><span className="title-icon">✓</span><div><h2>Confirm the requirement</h2><p>Review the assembled scope before planning is allowed to begin.</p></div></div>
              <div className="summary-paper"><span className="paper-label">REQUIREMENT SUMMARY</span><pre>{task.requirementSummary}</pre></div>
              <div className="approval-row"><p><strong>Explicit approval required</strong><br />Approval locks this summary as planning context.</p><button className="primary-button compact" onClick={approveSummary} disabled={busy}>{busy ? <><span className="spinner" />Approving…</> : <>Approve requirement <span>→</span></>}</button></div>
            </div>}
            {task?.status === 'ReadyForPlanning' && <div className="ready-view">
              <div className="success-seal">✓</div><p className="eyebrow">REQUIREMENT APPROVED</p><h2>Ready for planning</h2><p>The requirement context is confirmed and preserved. Repository-aware planning is the next implementation milestone.</p>
              <div className="coming-next"><span>04</span><div><strong>Planning</strong><p>Coming next · not implemented in this demo slice</p></div><button disabled>Not available yet</button></div><button className="text-button" onClick={startAnother}>Start another task</button>
            </div>}
          </section>
          <aside className="context-column">
            <section className="context-card"><div className="aside-title"><span>Repository context</span><i>{task ? 'CAPTURED' : 'WAITING'}</i></div>{task ? <><code>{task.repository}</code><p className="truth-note"><span>!</span> Path captured only. Repository inspection is not implemented yet.</p></> : <div className="empty-context"><span>⌘</span><p>Repository details will appear here after you create a task.</p></div>}</section>
            <section className="context-card history-card"><div className="aside-title"><span>Clarification history</span><b>{answeredCount}</b></div>{task && answeredCount > 0 ? task.clarificationAnswers.map((item, index) => <details key={item.answeredAt} open={index === answeredCount - 1}><summary><span>{String(index + 1).padStart(2, '0')}</span>{item.question}</summary><p>{item.answer}</p></details>) : <div className="empty-context compact"><p>Answers will be preserved here as you clarify the requirement.</p></div>}</section>
            <section className="telemetry-card"><div><span>MODEL CALLS</span><strong>0</strong></div><div><span>TOKENS</span><strong>0</strong></div><div><span>EST. COST</span><strong>$0.000</strong></div><p><i /> Deterministic demo adapter · no AI calls</p></section>
          </aside>
        </div>
      </main>
      <footer><span>FORGE AI · BUILD WEEK EDITION</span><p><i /> API connected <b>·</b> Local persistence <b>·</b> Development adapter</p></footer>
    </div>
  )
}

function formatStatus(status: WorkflowStatus) { return status.replace(/([a-z])([A-Z])/g, '$1 $2') }
export default App
