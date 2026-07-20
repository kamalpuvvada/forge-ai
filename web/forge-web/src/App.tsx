import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { ForgeApiError, forgeApi } from './api'
import { createPlanPdfDownloader, createTaskPdfDownloader, exportErrorMessage } from './pdfDownload'
import { getPlanningRecovery, getReadyPlanningAction } from './planningRecovery'
import { RequirementCopyButton } from './RequirementCopyButton'
import { createRequirementCopier } from './requirementCopy'
import type { RequirementCopyState } from './requirementCopy'
import { TaskHistory } from './TaskHistory'
import { TaskSelectionCoordinator, newTaskUrl, parseTaskSelection, taskUrl } from './taskNavigation'
import type { EngineeringTask, EngineeringTaskSummary, SystemCapabilities, WorkflowStatus } from './types'
import './App.css'
import './implementation.css'

const stages = ['Understand', 'Clarify', 'Confirm', 'Plan', 'Implement', 'Diff review', 'Validate', 'Review', 'Pull Request']
const stageByStatus: Record<WorkflowStatus, number> = { Draft: 0, Clarifying: 1, RequirementSummaryReady: 2, AwaitingRequirementApproval: 2, ReadyForPlanning: 3, Planning: 3, AwaitingPlanApproval: 3, PlanApproved: 3, Implementing: 4, AwaitingImplementationReview: 5, ImplementationApproved: 5, Validating: 6, Reviewing: 7, Completed: 8, Failed: 0 }

function App() {
  const [repository, setRepository] = useState('')
  const [requirement, setRequirement] = useState('')
  const [answer, setAnswer] = useState('')
  const [correction, setCorrection] = useState('')
  const [correctionMode, setCorrectionMode] = useState(false)
  const [planCorrection, setPlanCorrection] = useState('')
  const [planCorrectionMode, setPlanCorrectionMode] = useState(false)
  const [planRevisionInFlight, setPlanRevisionInFlight] = useState(false)
  const [task, setTask] = useState<EngineeringTask | null>(null)
  const [historyTasks, setHistoryTasks] = useState<EngineeringTaskSummary[]>([])
  const [historyLoading, setHistoryLoading] = useState(false)
  const [historyError, setHistoryError] = useState<string | null>(null)
  const [taskLoading, setTaskLoading] = useState(false)
  const [taskLoadState, setTaskLoadState] = useState<{ kind: 'invalid' | 'missing' | 'error'; requested: string; message: string } | null>(null)
  const [capabilities, setCapabilities] = useState<SystemCapabilities | null>(null)
  const [capabilitiesUnavailable, setCapabilitiesUnavailable] = useState(false)
  const [busy, setBusy] = useState(false)
  const [planningProgress, setPlanningProgress] = useState<number | null>(null)
  const [planningFailure, setPlanningFailure] = useState<string | null>(null)
  const [planningSnapshotStale, setPlanningSnapshotStale] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [exportingPdf, setExportingPdf] = useState<'task' | 'plan' | null>(null)
  const [pdfExportError, setPdfExportError] = useState<string | null>(null)
  const [implementationInFlight, setImplementationInFlight] = useState(false)
  const [approvalInFlight, setApprovalInFlight] = useState(false)
  const [approvalDialogOpen, setApprovalDialogOpen] = useState(false)
  const [copyState, setCopyState] = useState<RequirementCopyState>('idle')
  const [copyError, setCopyError] = useState<string | null>(null)
  const [historyPanelOpen, setHistoryPanelOpen] = useState(false)
  const pdfDownloader = useMemo(() => createTaskPdfDownloader(), [])
  const planPdfDownloader = useMemo(() => createPlanPdfDownloader(), [])
  const requirementCopier = useMemo(() => createRequirementCopier(), [])
  const selectionCoordinator = useRef(new TaskSelectionCoordinator())
  const copyResetTimer = useRef<number | null>(null)
  const historyToggleButton = useRef<HTMLButtonElement | null>(null)
  const historyPanelHeading = useRef<HTMLHeadingElement | null>(null)
  const approvalDialog = useRef<HTMLDialogElement | null>(null)
  const approvalCancelButton = useRef<HTMLButtonElement | null>(null)
  const approvalSubmissionActive = useRef(false)
  const selectedTaskId = task?.id ?? null
  const activeStage = task ? stageByStatus[task.status] : 0
  const answeredCount = task?.clarificationAnswers.length ?? 0
  const taskLabel = useMemo(() => task ? `FORGE-${task.id.slice(0, 6).toUpperCase()}` : 'NEW TASK', [task])

  useEffect(() => {
    let active = true
    forgeApi.getCapabilities()
      .then(value => {
        if (!active) return
        setCapabilities(value)
        setCapabilitiesUnavailable(false)
      })
      .catch(() => {
        if (!active) return
        setCapabilitiesUnavailable(true)
      })
    return () => { active = false }
  }, [])

  async function loadHistory() {
    setHistoryLoading(true); setHistoryError(null)
    try { setHistoryTasks(await forgeApi.listTasks()) }
    catch { setHistoryError('Task history is temporarily unavailable.') }
    finally { setHistoryLoading(false) }
  }

  function clearCopyResetTimer() {
    if (copyResetTimer.current === null) return
    window.clearTimeout(copyResetTimer.current)
    copyResetTimer.current = null
  }

  function resetTaskScopedUi() {
    clearCopyResetTimer()
    setBusy(false)
    setPlanningProgress(null)
    setPlanningFailure(null)
    setPlanningSnapshotStale(false)
    setError(null)
    setExportingPdf(null)
    setPdfExportError(null)
    setCopyState('idle')
    setCopyError(null)
    setPlanRevisionInFlight(false)
    setImplementationInFlight(false)
    setApprovalInFlight(false)
    approvalSubmissionActive.current = false
    setApprovalDialogOpen(false)
  }

  function captureTaskSelection(taskId: string) {
    const token = selectionCoordinator.current.capture()
    return token.taskId === taskId ? token : null
  }

  async function loadSelection(search: string) {
    const selection = parseTaskSelection(search)
    const token = selectionCoordinator.current.begin(selection.kind === 'task' ? selection.id : null)
    resetTaskScopedUi()
    if (selection.kind === 'new') {
      setTask(null); setTaskLoadState(null); setTaskLoading(false)
      await loadHistory(); return
    }
    if (selection.kind === 'invalid') {
      setTask(null); setTaskLoading(false)
      setTaskLoadState({ kind: 'invalid', requested: selection.requested, message: 'The task link contains an invalid task identifier.' }); return
    }
    setTask(null); setTaskLoading(true); setTaskLoadState(null)
    try {
      const loaded = await forgeApi.getTask(selection.id)
      if (!selectionCoordinator.current.matches(token)) return
      setTaskLoading(false)
      setTask(loaded)
    } catch (caught) {
      if (!selectionCoordinator.current.matches(token)) return
      setTaskLoading(false)
      const missing = caught instanceof ForgeApiError && caught.code === 'task_not_found'
      setTaskLoadState({
        kind: missing ? 'missing' : 'error',
        requested: selection.id,
        message: missing ? 'The requested task was not found.' : 'The requested task could not be loaded.',
      })
    }
  }

  useEffect(() => {
    const coordinator = selectionCoordinator.current
    const handleLocation = () => { void loadSelection(window.location.search) }
    handleLocation()
    window.addEventListener('popstate', handleLocation)
    return () => {
      window.removeEventListener('popstate', handleLocation)
      coordinator.invalidate()
    }
  }, [])

  useEffect(() => {
    return clearCopyResetTimer
  }, [])

  useEffect(() => {
    setHistoryPanelOpen(false)
  }, [selectedTaskId])

  useEffect(() => {
    if (selectedTaskId === null || !historyPanelOpen) return
    void loadHistory()
  }, [historyPanelOpen, selectedTaskId])

  useEffect(() => {
    if (!historyPanelOpen) return
    historyPanelHeading.current?.focus()
    const button = historyToggleButton.current
    const handleEscape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      event.preventDefault()
      setHistoryPanelOpen(false)
      button?.focus()
    }

    window.addEventListener('keydown', handleEscape)
    return () => window.removeEventListener('keydown', handleEscape)
  }, [historyPanelOpen])

  useEffect(() => {
    if (!approvalDialogOpen) return
    const nativeDialogSupported = typeof HTMLDialogElement !== 'undefined'
      && typeof HTMLDialogElement.prototype.showModal === 'function'
    const dialog = approvalDialog.current
    if (nativeDialogSupported && dialog && !dialog.open) dialog.showModal()
    approvalCancelButton.current?.focus()
  }, [approvalDialogOpen])

  async function runTaskAction(taskId: string, action: () => Promise<EngineeringTask>, onSuccess?: () => void) {
    const token = captureTaskSelection(taskId)
    if (!token) return
    setBusy(true); setError(null)
    try {
      const updated = await action()
      if (!selectionCoordinator.current.matches(token)) return
      setTask(updated)
      onSuccess?.()
    }
    catch (caught) {
      if (!selectionCoordinator.current.matches(token)) return
      setError(caught instanceof Error ? caught.message : 'An unexpected error occurred.')
    }
    finally {
      if (selectionCoordinator.current.matches(token)) setBusy(false)
    }
  }

  function createTask(event: FormEvent) {
    event.preventDefault()
    const token = selectionCoordinator.current.capture()
    if (token.taskId !== null) return
    setBusy(true); setError(null)
    void (async () => {
      const created = await forgeApi.createTask(repository, requirement)
      if (!selectionCoordinator.current.matches(token)) return
      setBusy(false)
      window.history.pushState(null, '', taskUrl(created.id))
      selectionCoordinator.current.begin(created.id)
      setTask(created)
      setTaskLoadState(null)
      setTaskLoading(false)
    })().catch(caught => {
      if (!selectionCoordinator.current.matches(token)) return
      setError(caught instanceof Error ? caught.message : 'An unexpected error occurred.')
      setBusy(false)
    })
  }
  function submitAnswer(event: FormEvent) {
    event.preventDefault(); if (!task) return
    const submitted = answer
    void runTaskAction(task.id, () => forgeApi.answerQuestion(task.id, submitted), () => setAnswer(''))
  }
  function submitCorrection(event: FormEvent) {
    event.preventDefault(); if (!task) return
    const submitted = correction
    void runTaskAction(task.id, () => forgeApi.requestRevision(task.id, submitted), () => {
      setCorrection('')
      setCorrectionMode(false)
    })
  }
  function approveSummary() { if (task) void runTaskAction(task.id, () => forgeApi.approveRequirement(task.id)) }
  async function analyzeAndPlan() {
    if (!task) return
    const token = captureTaskSelection(task.id)
    if (!token) return
    let repositoryAnalyzed = false
    setBusy(true); setError(null); setPlanningFailure(null); setPlanningSnapshotStale(false); setPlanningProgress(0)
    try {
      const analyzed = await forgeApi.analyzeRepository(task.id)
      if (!selectionCoordinator.current.matches(token)) return
      repositoryAnalyzed = true; setTask(analyzed); setPlanningProgress(2)
      const planned = await forgeApi.createPlan(task.id)
      if (!selectionCoordinator.current.matches(token)) return
      setTask(planned); setPlanningProgress(3)
    } catch (caught) {
      if (!selectionCoordinator.current.matches(token)) return
      const message = caught instanceof Error ? caught.message : 'Planning could not be completed.'
      if (repositoryAnalyzed) {
        try {
          const refreshed = await forgeApi.getTask(task.id)
          if (!selectionCoordinator.current.matches(token)) return
          setTask(refreshed)
        } catch {
          if (!selectionCoordinator.current.matches(token)) return
        }
        setPlanningSnapshotStale(caught instanceof ForgeApiError && caught.code === 'stale_snapshot')
        setPlanningFailure(message)
      }
      else setError(message)
    }
    finally {
      if (selectionCoordinator.current.matches(token)) setBusy(false)
    }
  }
  async function retryPlanGeneration() {
    if (!task || (task.status !== 'Planning' && task.status !== 'ReadyForPlanning')) return
    const token = captureTaskSelection(task.id)
    if (!token) return
    setBusy(true); setError(null); setPlanningFailure(null); setPlanningSnapshotStale(false); setPlanningProgress(2)
    try {
      const planned = await forgeApi.createPlan(task.id)
      if (!selectionCoordinator.current.matches(token)) return
      setTask(planned); setPlanningProgress(3)
    } catch (caught) {
      if (!selectionCoordinator.current.matches(token)) return
      try {
        const refreshed = await forgeApi.getTask(task.id)
        if (!selectionCoordinator.current.matches(token)) return
        setTask(refreshed)
      } catch {
        if (!selectionCoordinator.current.matches(token)) return
      }
      setPlanningSnapshotStale(caught instanceof ForgeApiError && caught.code === 'stale_snapshot')
      setPlanningFailure(caught instanceof Error ? caught.message : 'Planning could not be completed.')
    } finally {
      if (selectionCoordinator.current.matches(token)) setBusy(false)
    }
  }
  async function refreshEvidence() {
    if (!task || task.status !== 'Planning') return
    const token = captureTaskSelection(task.id)
    if (!token) return
    setBusy(true); setError(null); setPlanningFailure(null); setPlanningSnapshotStale(false)
    try {
      const refreshed = await forgeApi.refreshEvidence(task.id)
      if (!selectionCoordinator.current.matches(token)) return
      setTask(refreshed)
    } catch (caught) {
      if (!selectionCoordinator.current.matches(token)) return
      try {
        const refreshed = await forgeApi.getTask(task.id)
        if (!selectionCoordinator.current.matches(token)) return
        setTask(refreshed)
      } catch {
        if (!selectionCoordinator.current.matches(token)) return
      }
      setPlanningSnapshotStale(caught instanceof ForgeApiError && caught.code === 'stale_snapshot')
      setPlanningFailure(caught instanceof Error ? caught.message : 'Repository evidence could not be refreshed.')
    } finally {
      if (selectionCoordinator.current.matches(token)) setBusy(false)
    }
  }
  async function submitPlanCorrection(event: FormEvent) {
    event.preventDefault()
    if (!task || task.status !== 'AwaitingPlanApproval' || !planCorrection.trim()) return
    const token = captureTaskSelection(task.id)
    if (!token) return
    const submitted = planCorrection
    setBusy(true); setPlanRevisionInFlight(true); setError(null); setPlanningFailure(null); setPlanningSnapshotStale(false)
    try {
      const revised = await forgeApi.requestPlanRevision(task.id, submitted)
      if (!selectionCoordinator.current.matches(token)) return
      setTask(revised); setPlanCorrection(''); setPlanCorrectionMode(false)
    } catch (caught) {
      if (!selectionCoordinator.current.matches(token)) return
      try {
        const refreshed = await forgeApi.getTask(task.id)
        if (!selectionCoordinator.current.matches(token)) return
        setTask(refreshed)
      } catch {
        if (!selectionCoordinator.current.matches(token)) return
      }
      setPlanningSnapshotStale(caught instanceof ForgeApiError && caught.code === 'stale_snapshot')
      setPlanningFailure(caught instanceof Error ? caught.message : 'The plan correction could not be completed.')
    } finally {
      if (selectionCoordinator.current.matches(token)) {
        setPlanRevisionInFlight(false)
        setBusy(false)
      }
    }
  }
  function approvePlan() { if (task) void runTaskAction(task.id, () => forgeApi.approvePlan(task.id)) }
  async function generateImplementation() {
    if (!task || (task.status !== 'PlanApproved' && task.status !== 'Implementing')) return
    const token = captureTaskSelection(task.id)
    if (!token) return
    setBusy(true); setImplementationInFlight(true); setError(null)
    try {
      const implemented = await forgeApi.generateImplementation(task.id)
      if (!selectionCoordinator.current.matches(token)) return
      setTask(implemented)
    } catch (caught) {
      if (!selectionCoordinator.current.matches(token)) return
      try {
        const refreshed = await forgeApi.getTask(task.id)
        if (!selectionCoordinator.current.matches(token)) return
        setTask(refreshed)
      } catch { /* Preserve the safe endpoint error below. */ }
      if (!selectionCoordinator.current.matches(token)) return
      setError(caught instanceof Error ? caught.message : 'Implementation generation could not be completed.')
    } finally {
      if (selectionCoordinator.current.matches(token)) {
        setBusy(false)
        setImplementationInFlight(false)
      }
    }
  }
  function closeApprovalDialog() {
    const dialog = approvalDialog.current
    if (dialog?.open && typeof dialog.close === 'function') dialog.close()
    setApprovalDialogOpen(false)
  }
  async function approveImplementation() {
    if (approvalSubmissionActive.current || !task || task.status !== 'AwaitingImplementationReview' ||
      task.implementationResult?.activeCheckoutVerified !== true) return
    const revision = task.implementationRevisions.find(item => item.revisionId === task.activeImplementationRevisionId)
    if (!revision?.resultFingerprint) return
    const token = captureTaskSelection(task.id)
    if (!token) return
    approvalSubmissionActive.current = true
    closeApprovalDialog()
    setBusy(true); setApprovalInFlight(true); setError(null)
    try {
      const approved = await forgeApi.approveImplementation(task.id, {
        commandId: crypto.randomUUID(),
        expectedRowVersion: task.rowVersion,
        expectedRevisionId: revision.revisionId,
        expectedResultFingerprint: revision.resultFingerprint,
      })
      if (!selectionCoordinator.current.matches(token)) return
      setTask(approved)
    } catch (caught) {
      if (!selectionCoordinator.current.matches(token)) return
      if (caught instanceof ForgeApiError && ['task_concurrency_conflict', 'workflow_conflict'].includes(caught.code ?? '')) {
        try {
          const refreshed = await forgeApi.getTask(task.id)
          if (!selectionCoordinator.current.matches(token)) return
          setTask(refreshed)
        } catch { /* Keep the safe conflict message below. */ }
        if (!selectionCoordinator.current.matches(token)) return
        setError('The implementation review changed while approval was pending. Review the current revision before trying again.')
      } else {
        setError(caught instanceof Error ? caught.message : 'The implementation review could not be approved.')
      }
    } finally {
      if (selectionCoordinator.current.matches(token)) {
        setBusy(false)
        setApprovalInFlight(false)
        approvalSubmissionActive.current = false
      }
    }
  }
  async function exportPdf(documentType: 'task' | 'plan') {
    const approvedPlanState = task?.implementationPlan && task.planApprovedAt
    if (!task || exportingPdf || (documentType === 'task' && !approvedPlanState)) return
    const token = captureTaskSelection(task.id)
    if (!token) return
    const downloader = documentType === 'task' ? pdfDownloader : planPdfDownloader
    if (downloader.isActive) return
    setExportingPdf(documentType); setPdfExportError(null)
    try { await downloader.run(task.id) }
    catch (caught) {
      if (!selectionCoordinator.current.matches(token)) return
      setPdfExportError(exportErrorMessage(caught))
    }
    finally {
      if (selectionCoordinator.current.matches(token)) setExportingPdf(null)
    }
  }
  async function copyRequirement() {
    if (!task?.requirementSummary || requirementCopier.isPending) return
    const token = captureTaskSelection(task.id)
    if (!token) return
    setCopyState('pending'); setCopyError(null)
    try {
      await requirementCopier.run(task.requirementSummary)
      if (!selectionCoordinator.current.matches(token)) return
      setCopyState('copied')
      clearCopyResetTimer()
      copyResetTimer.current = window.setTimeout(() => setCopyState('idle'), 2500)
    } catch {
      if (!selectionCoordinator.current.matches(token)) return
      setCopyState('error')
      setCopyError('The requirement could not be copied. Check clipboard permission and try again.')
    }
  }
  function navigateTo(url: string) {
    setHistoryPanelOpen(false)
    window.history.pushState(null, '', url)
    void loadSelection(window.location.search)
  }
  function selectHistoryTask(id: string) { navigateTo(taskUrl(id)) }
  function startAnother() {
    setRepository(''); setRequirement(''); setAnswer(''); setCorrection(''); setCorrectionMode(false); setPlanCorrection(''); setPlanCorrectionMode(false)
    navigateTo(newTaskUrl())
  }

  const telemetry = task?.telemetry ?? { totalCalls: 0, totalInputTokens: 0, totalCachedInputTokens: 0, totalOutputTokens: 0, totalEstimatedCostUsd: 0, costUnavailableCallCount: 0, isPartialEstimate: false, calls: [] }
  const clarificationCalls = telemetry.calls.filter(call => call.stage === 'Clarification')
  const planningCalls = telemetry.calls.filter(call => call.stage === 'Planning')
  const implementationCalls = telemetry.calls.filter(call => call.stage === 'Implementation')
  const implementationDisposition = task?.implementationRuntime?.disposition ?? (task?.lastImplementationFailure?.recoveryRequired
    ? 'RecoveryRequired'
    : task?.lastImplementationFailure?.safeToResume ? 'SafeResume' : 'Interrupted')
  const implementationIsActive = task?.status === 'Implementing' && implementationDisposition === 'Active'
  const implementationCanResume = task?.status === 'Implementing' && implementationDisposition === 'SafeResume'
  const activeCheckoutVerified = task?.implementationRuntime?.activeCheckoutVerified
    ?? task?.implementationResult?.activeCheckoutVerified
    ?? task?.lastImplementationFailure?.activeCheckoutVerified
    ?? false
  const persistedCheckoutVerified = task?.implementationResult?.activeCheckoutVerified === true
  const hasApprovedPlan = Boolean(task?.implementationPlan && task.planApprovedAt)
  const approvedPlanControlsRenderedInState = task ? ['PlanApproved', 'Implementing', 'AwaitingImplementationReview', 'ImplementationApproved'].includes(task.status) : false
  const activeImplementationRevision = task?.implementationRevisions.find(revision => revision.revisionId === task.activeImplementationRevisionId) ?? null
  const nativeApprovalDialogSupported = typeof HTMLDialogElement !== 'undefined'
    && typeof HTMLDialogElement.prototype.showModal === 'function'
  const approvalDialogContents = <>
    <h2 id="implementation-approval-title">Approve implementation revision {activeImplementationRevision?.revisionNumber}</h2>
    <p id="implementation-approval-description">Fingerprint: <code>{activeImplementationRevision?.resultFingerprint?.slice(0, 12)}…</code></p>
    <ul><li>Validation was not run.</li><li>No files were staged.</li><li>No commit or push occurred.</li><li>Approval accepts the persisted review only.</li></ul>
    <div className="approval-actions"><button ref={approvalCancelButton} type="button" className="secondary-button" onClick={closeApprovalDialog}>Cancel</button><button type="button" className="primary-button compact" onClick={() => void approveImplementation()} disabled={approvalInFlight}>{approvalInFlight ? 'Approving…' : 'Confirm approval'}</button></div>
  </>
  const planningCost = planningCalls.reduce((total, call) => total + (call.estimatedCostUsd ?? 0), 0)
  const latestFailedPlanningCall = [...planningCalls].reverse().find(call => !call.succeeded)
  const planningRecovery = getPlanningRecovery(latestFailedPlanningCall?.failureCategory ?? null, planningSnapshotStale)
  const readyPlanningAction = getReadyPlanningAction((task?.evidenceItems.length ?? 0) > 0)
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
          {task && <div className="task-navigation-strip">
            <div className="task-navigation-actions">
              <button
                ref={historyToggleButton}
                type="button"
                className="secondary-button"
                aria-expanded={historyPanelOpen}
                aria-controls="selected-task-history"
                onClick={() => setHistoryPanelOpen(open => !open)}>
                Task history
              </button>
              <button type="button" className="secondary-button" onClick={startAnother}>New task</button>
            </div>
            <p>Switch tasks or return to the blank task form without changing persisted task data.</p>
          </div>}
          {task && historyPanelOpen && <section className="selected-task-history-panel" id="selected-task-history" aria-labelledby="selected-task-history-heading">
            <div className="selected-task-history-header">
              <div>
                <p className="eyebrow">TASK NAVIGATION</p>
                <h2 id="selected-task-history-heading" tabIndex={-1} ref={historyPanelHeading}>Recent tasks</h2>
              </div>
              <button type="button" className="text-button" onClick={() => { setHistoryPanelOpen(false); historyToggleButton.current?.focus() }}>
                Close history
              </button>
            </div>
            <TaskHistory tasks={historyTasks} loading={historyLoading} error={historyError} selectedId={task.id} onSelect={selectHistoryTask} onRetry={() => void loadHistory()} />
          </section>}
          {!task && taskLoading && <div className="task-load-state" role="status"><span className="spinner dark" /><h2>Loading persisted task…</h2><p>The task is being reopened without changing its workflow state.</p></div>}
          {!task && taskLoadState && <div className="task-load-state" role="alert"><div className="failure-seal">!</div><h2>{taskLoadState.kind === 'invalid' ? 'Invalid task link' : taskLoadState.kind === 'missing' ? 'Task not found' : 'Task unavailable'}</h2><p>{taskLoadState.message}</p><code>{taskLoadState.requested || '(empty task identifier)'}</code><div className="load-state-actions">{taskLoadState.kind !== 'invalid' && <button type="button" className="secondary-button" onClick={() => void loadSelection(window.location.search)}>Retry task</button>}<button type="button" className="primary-button compact" onClick={startAnother}>Return to new task</button></div></div>}
          {!task && !taskLoading && !taskLoadState && <><form className="task-form" onSubmit={createTask}>
            <div className="action-title"><span className="title-icon">⌁</span><div><h2>What are we building?</h2><p>Point Forge at a local repository and describe the outcome you need.</p></div></div>
            <label><span>LOCAL REPOSITORY PATH</span><div className="input-frame"><span aria-hidden="true">⌘</span><input value={repository} onChange={event => setRepository(event.target.value)} placeholder="C:\Projects\your-repository" required /></div></label>
            <label><span>REQUIREMENT OR WORK ITEM</span><textarea value={requirement} onChange={event => setRequirement(event.target.value)} placeholder="Describe the change, why it matters, and any known constraints…" rows={7} maxLength={10000} required /><small>{requirement.length.toLocaleString()} / 10,000</small></label>
            <button className="primary-button" disabled={busy || !repository.trim() || !requirement.trim()}>{busy ? <><span className="spinner" />Evaluating…</> : <>Analyze requirement <span>→</span></>}</button>
            <p className="form-note"><span>i</span> Repository inspection occurs read-only only after requirement approval.</p>
          </form><TaskHistory tasks={historyTasks} loading={historyLoading} error={historyError} selectedId={null} onSelect={selectHistoryTask} onRetry={() => void loadHistory()} /></>}
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
            <div className="summary-paper"><div className="paper-toolbar"><span className="paper-label">CURRENT REQUIREMENT SUMMARY</span><RequirementCopyButton state={copyState} onCopy={() => void copyRequirement()} /></div><pre>{task.requirementSummary}</pre>{copyState === 'copied' && <p className="copy-success" role="status">Copied to clipboard.</p>}{copyError && <p className="copy-error" role="alert">{copyError}</p>}</div>
            {!correctionMode ? <div className="approval-row"><p><strong>Explicit approval required</strong><br />Approval locks this summary as planning context.</p><div className="approval-actions"><button className="secondary-button" onClick={() => setCorrectionMode(true)}>Request correction</button><button className="primary-button compact" onClick={approveSummary} disabled={busy}>Approve requirement <span>→</span></button></div></div>
              : <form className="correction-form" onSubmit={submitCorrection}><div><strong>Request a correction</strong><p>The current summary will be preserved in revision history and regenerated.</p></div><label><span>CORRECTION NOTE</span><textarea autoFocus value={correction} onChange={event => setCorrection(event.target.value)} placeholder="State only what should change…" rows={4} maxLength={5000} required /></label><div className="approval-actions"><button type="button" className="secondary-button" onClick={() => { setCorrectionMode(false); setCorrection('') }}>Cancel</button><button className="primary-button compact" disabled={busy || !correction.trim()}>{busy ? 'Regenerating…' : 'Submit correction'}</button></div></form>}
          </div>}
          {task?.status === 'ReadyForPlanning' && <div className="ready-view"><div className="success-seal">✓</div><p className="eyebrow">{readyPlanningAction.eyebrow}</p><h2>Ready for evidence-backed planning</h2><p>{readyPlanningAction.usesSavedEvidence ? 'The refreshed evidence is saved. Generate the plan only when you are ready to make one explicit provider call.' : 'Forge will inspect the repository read-only, select bounded evidence, and use the configured planning provider.'}</p><div className="coming-next"><span>04</span><div><strong>Read-only planning</strong><p>No files, Git state, packages, builds, or tests will be changed.</p></div><button className="primary-button compact" onClick={() => void (readyPlanningAction.usesSavedEvidence ? retryPlanGeneration() : analyzeAndPlan())} disabled={busy}>{readyPlanningAction.button}</button></div></div>}
          {task?.status === 'Planning' && planningFailureMessage && !busy && <div className="planning-failure"><div className="failure-seal">!</div><p className="eyebrow">PLAN GENERATION FAILED</p><h2>{planningRecovery.heading}</h2><p>{planningFailureMessage}</p>{latestFailedPlanningCall && <div className="failed-call-summary"><strong>{latestFailedPlanningCall.model} · {latestFailedPlanningCall.failureCategory}</strong><span>{(latestFailedPlanningCall.outputTokens ?? 0).toLocaleString()} output tokens · {latestFailedPlanningCall.estimatedCostUsd === null ? 'cost unavailable' : `estimated $${latestFailedPlanningCall.estimatedCostUsd.toFixed(6)}`}</span></div>}{planningRecovery.action === 'reanalyze' ? <button className="primary-button compact" onClick={() => void analyzeAndPlan()} disabled={busy}>Re-analyze repository and create plan <span>→</span></button> : planningRecovery.action === 'refresh' ? <button className="primary-button compact" onClick={() => void refreshEvidence()} disabled={busy}>Refresh evidence <span>→</span></button> : <button className="primary-button compact" onClick={() => void retryPlanGeneration()} disabled={busy}>Retry plan generation <span>→</span></button>}<small>{planningRecovery.note}</small></div>}
          {task?.status === 'Planning' && task.planRevisionNotes.length > 0 && !busy && <section className="plan-revision-history"><small>PREVIOUS PLAN PRESERVED</small>{task.planRevisionNotes.map((revision, index) => <article key={revision.submittedAt}><strong>Revision {index + 1}: {revision.previousPlanTitle}</strong><p>{revision.correction}</p><code>{revision.previousAffectedPaths.join(', ') || 'No affected paths'}</code></article>)}</section>}
          {((task?.status === 'Planning' && !planningFailureMessage) || planningProgress !== null && busy) && <div className="planning-progress"><div className="action-title"><span className="title-icon">04</span><div><h2>Creating an evidence-backed plan</h2><p>The target remains read-only throughout analysis.</p></div></div>{['Inspecting repository', 'Selecting evidence', 'Creating evidence-backed plan', 'Awaiting approval'].map((label, index) => <div className={`planning-progress-step ${index < (planningProgress ?? 0) ? 'complete' : index === (planningProgress ?? 0) ? 'active' : ''}`} key={label}><span>{index < (planningProgress ?? 0) ? '✓' : index + 1}</span><p>{label}</p></div>)}</div>}
          {task?.status === 'AwaitingPlanApproval' && task.implementationPlan && <div className="plan-view">
            <div className="action-title"><span className="title-icon">✓</span><div><h2>Review the evidence-backed plan</h2><p>Every existing file proposal is linked to selected repository evidence.</p></div></div>
            <span className="fake-label">{task.implementationPlan.source === 'OpenAI' ? `OpenAI plan · ${task.implementationPlan.planningModel}` : 'Deterministic Fake plan'} · NO CODE CHANGED</span>
            {task.planRevisionNotes.length > 0 && <section className="plan-revision-history"><small>PLAN REVISION HISTORY</small>{task.planRevisionNotes.map((revision, index) => <article key={revision.submittedAt}><strong>Revision {index + 1}: {revision.previousPlanTitle}</strong><p>{revision.correction}</p><code>{revision.previousAffectedPaths.join(', ') || 'No affected paths'}</code></article>)}</section>}
            <section className="plan-section"><small>OBJECTIVE</small><p>{task.implementationPlan.objective}</p></section>
            <section className="plan-section"><small>REPOSITORY UNDERSTANDING</small><p>{task.implementationPlan.repositoryUnderstanding}</p></section>
            <section className="plan-section"><small>AFFECTED FILES</small>{task.implementationPlan.affectedFiles.map(file => <article className="planned-file" key={`${file.action}-${file.path}`}><div><code>{file.path}</code><span>{file.action}</span></div><p>{file.purpose}</p><small>Evidence: {file.evidenceIds.join(', ') || 'none'} · confidence {Math.round(file.confidence * 100)}%</small></article>)}</section>
            <section className="plan-section"><small>ORDERED STEPS</small><ol>{task.implementationPlan.orderedSteps.map(step => <li key={step.order}><strong>{step.description}</strong><p>{step.expectedResult}</p><small>Paths: {step.affectedPaths.join(', ')} · Evidence: {step.evidenceIds.join(', ') || 'none'}</small></li>)}</ol></section>
            <section className="plan-section requirement-coverage"><small>REQUIREMENT COVERAGE</small>{task.implementationPlan.requirementCoverage.map((item, index) => <article key={`${index}-${item.requirement}`}><strong>{item.requirement}</strong><p>Paths: {item.affectedPaths.join(', ')}</p><small>Steps: {item.stepOrders.join(', ')}</small></article>)}</section>
            <section className="plan-section"><small>PROPOSED VALIDATION · NOT RUN</small>{task.implementationPlan.proposedValidationCommands.map(command => <code className="validation-command" key={command}>{command}</code>)}</section>
            <div className="plan-columns"><section className="plan-section"><small>RISKS</small><ul>{task.implementationPlan.risks.map(risk => <li key={risk}>{risk}</li>)}</ul></section><section className="plan-section"><small>ASSUMPTIONS</small><ul>{task.implementationPlan.assumptions.map(assumption => <li key={assumption}>{assumption}</li>)}</ul></section></div>
            {task.implementationPlan.unresolvedQuestions.length > 0 && <section className="plan-section"><small>UNRESOLVED QUESTIONS</small><ul>{task.implementationPlan.unresolvedQuestions.map(question => <li key={question}>{question}</li>)}</ul></section>}
            <div className="plan-download-row"><div><strong>Proposed plan document</strong><p>Exports this persisted plan marked as not approved.</p></div><button type="button" className="secondary-button" onClick={() => void exportPdf('plan')} disabled={exportingPdf !== null}>{exportingPdf === 'plan' ? 'Generating proposed plan…' : 'Download proposed plan'}</button></div>
            {pdfExportError && <p className="export-error" role="alert">{pdfExportError}</p>}
            {!planCorrectionMode ? <div className="approval-row"><p><strong>Explicit plan approval required</strong><br />Approve this plan or request one focused correction.</p><div className="approval-actions"><button className="secondary-button" onClick={() => void analyzeAndPlan()} disabled={busy}>Re-analyze repository</button><button className="secondary-button" onClick={() => setPlanCorrectionMode(true)} disabled={busy}>Request plan correction</button><button className="primary-button compact" onClick={approvePlan} disabled={busy}>Approve plan <span>→</span></button></div></div>
              : <form className="correction-form plan-correction-form" onSubmit={submitPlanCorrection}><div><strong>Request one focused plan correction</strong><p>Forge will reuse the fresh snapshot, refresh only relevant evidence, preserve this plan in history, and make exactly one planning request.</p></div><label><span>PLAN CORRECTION</span><textarea autoFocus value={planCorrection} onChange={event => setPlanCorrection(event.target.value)} placeholder="State the specific gap to correct without restating the full requirement…" rows={5} maxLength={5000} required /><small>{planCorrection.length.toLocaleString()} / 5,000</small></label>{planRevisionInFlight && <div className="revision-progress" aria-live="polite"><span>Checking saved snapshot</span><span>Refreshing targeted evidence</span><span>Generating revised plan</span></div>}<div className="approval-actions"><button type="button" className="secondary-button" onClick={() => { setPlanCorrectionMode(false); setPlanCorrection('') }} disabled={busy}>Cancel</button><button className="primary-button compact" disabled={busy || !planCorrection.trim()}>{planRevisionInFlight ? <><span className="spinner" />Revising plan…</> : 'Submit correction'}</button></div></form>}
          </div>}
          {task?.status === 'PlanApproved' && <div className="ready-view"><div className="success-seal">✓</div><p className="eyebrow">PLAN APPROVED</p><h2>Generate an isolated implementation for diff review.</h2><p>Forge will require a clean Git repository at the approved HEAD, create a task branch and linked worktree outside the active checkout, then apply deterministic Fake changes only in that worktree.</p><div className="coming-next"><span>05</span><div><strong>Safe Fake implementation</strong><p>No validation, staging, commit, push or pull request action will run.</p></div>{capabilities?.implementationConfigured ? <button className="primary-button compact" onClick={() => void generateImplementation()} disabled={busy}>{implementationInFlight ? <><span className="spinner" />Generating…</> : <>Generate implementation <span>→</span></>}</button> : <p className="truth-note" role="status">Implementation generation is currently available only when Forge is configured in deterministic Fake mode.</p>}</div><div className="export-actions"><button className="secondary-button" onClick={() => void exportPdf('plan')} disabled={exportingPdf !== null}>{exportingPdf === 'plan' ? 'Generating plan…' : 'Download approved plan'}</button><button className="secondary-button" onClick={() => void exportPdf('task')} disabled={exportingPdf !== null}>{exportingPdf === 'task' ? 'Generating task report…' : 'Download task report PDF'}</button></div>{pdfExportError && <p className="export-error" role="alert">{pdfExportError}</p>}</div>}
          {implementationIsActive && <div className="implementation-progress" role="status"><span className="spinner dark" /><p className="eyebrow">ISOLATED IMPLEMENTATION</p><h2>Preparing generated changes for diff review</h2><p>Forge holds a valid implementation lease and is working only in the persisted task worktree.</p></div>}
          {task?.status === 'Implementing' && !implementationIsActive && <div className="planning-failure implementation-failure"><div className="failure-seal">!</div><p className="eyebrow">IMPLEMENTATION ATTEMPT IS NOT ACTIVE</p><h2>{implementationCanResume ? 'The isolated workspace can be safely resumed' : implementationDisposition === 'TerminalIncompatible' ? 'The approved plan is not compatible with Fake implementation' : 'Workspace recovery is required'}</h2><p>{task.implementationRuntime?.safeMessage ?? task.lastImplementationFailure?.message ?? 'The previous implementation attempt was interrupted.'}</p><small>{implementationCanResume ? 'Forge will verify the matching untouched workspace before it resumes.' : 'Forge will not reset, delete, or overwrite this workspace automatically.'}</small>{implementationCanResume && <button className="primary-button compact" onClick={() => void generateImplementation()} disabled={busy}>Resume implementation <span>→</span></button>}<div className="export-actions"><button className="secondary-button" onClick={() => void exportPdf('plan')} disabled={exportingPdf !== null}>Download approved plan</button><button className="secondary-button" onClick={() => void exportPdf('task')} disabled={exportingPdf !== null}>Download task report PDF</button></div></div>}
          {(task?.status === 'AwaitingImplementationReview' || task?.status === 'ImplementationApproved') && task.implementationResult && <div className="implementation-review">
            <div className="action-title"><span className="title-icon">06</span><div><h2>{task.status === 'ImplementationApproved' ? 'Implementation approved' : 'Review the isolated generated changes'}</h2><p>No validation commands were run and no change was staged, committed, pushed, or proposed as a pull request.</p></div></div>
            <span className="fake-label">Deterministic Fake implementation · MECHANICAL WORKFLOW DEMONSTRATION</span>
            {task.status === 'ImplementationApproved' && activeImplementationRevision && <div className="implementation-approved" role="status"><strong>Implementation approved</strong><p>Revision {activeImplementationRevision.revisionNumber} was approved on {activeImplementationRevision.approvedAt ? new Date(activeImplementationRevision.approvedAt).toLocaleString() : 'an unrecorded date'}.</p><code>{activeImplementationRevision.resultFingerprint?.slice(0, 12)}…</code><small>This approval accepts persisted review evidence only. Validation was not run, and nothing was staged, committed, pushed, or submitted as a pull request.</small></div>}
            {!persistedCheckoutVerified && <div className="diff-truncation-banner" role="alert"><strong>This review cannot be approved.</strong><p>Forge could not verify that the active checkout remained unchanged when implementation completed. This persisted completion evidence is not safely approvable.</p></div>}
            <div className="implementation-truth"><p><strong>Validation commands were not run.</strong></p><p><strong>{activeCheckoutVerified ? 'The active checkout was verified unchanged.' : 'The active checkout could not be verified unchanged.'}</strong></p><p>Workspace: {task.implementationRuntime ? task.implementationRuntime.workspaceAvailable ? 'available' : 'unavailable; persisted review remains readable' : 'not observed during approval; persisted review remains readable'}</p></div>
            <section className="plan-section implementation-metadata"><small>IMPLEMENTATION METADATA</small><dl><div><dt>Base commit</dt><dd><code>{task.implementationResult.baseCommitSha}</code></dd></div><div><dt>Generated branch</dt><dd><code>{task.implementationResult.branch}</code></dd></div><div><dt>Source</dt><dd>Deterministic Fake implementation</dd></div></dl></section>
            <section className="plan-section"><small>SUMMARY</small><p>{task.implementationResult.summary}</p></section>
            <section className="plan-section"><small>WARNINGS</small><ul>{task.implementationResult.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul></section>
            {task.implementationResult.diffTruncated && <div className="diff-truncation-banner" role="status"><strong>Displayed diff is incomplete.</strong><p>{task.implementationResult.displayedDiffCharacters.toLocaleString()} of {task.implementationResult.fullDiffCharacters.toLocaleString()} diff characters are persisted for display. File hashes and line counts are complete.</p></div>}
            <section className="plan-section changed-files"><small>CHANGED FILES</small>{task.implementationResult.changedFiles.map(file => <details className="changed-file-review" key={file.path}><summary><code>{file.path}</code><span>{file.action}</span><b className="diff-counts">+{file.additions} −{file.deletions}</b></summary><div className="changed-file-metadata"><span>{file.originalLines} → {file.newLines} lines</span><span>{file.originalBytes.toLocaleString()} → {file.newBytes.toLocaleString()} bytes</span></div>{file.diffTruncated && <div className="diff-truncation-banner compact"><strong>This file diff is truncated.</strong><span>{file.displayedDiffCharacters.toLocaleString()} of {file.fullDiffCharacters.toLocaleString()} characters displayed.</span></div>}<pre className="unified-diff" tabIndex={0} aria-label={`Unified diff for ${file.path}`}>{file.diffPreview}</pre></details>)}</section>
            <details className="implementation-revision-history"><summary>Implementation revision history</summary>{task.implementationRevisions.map(revision => <article key={revision.revisionId}><div><strong>Revision {revision.revisionNumber} · {revision.kind}</strong>{revision.isCurrent && <span>CURRENT</span>}{revision.isApproved && <span>APPROVED</span>}</div><p>{revision.generationState} · {revision.reviewState} · {revision.changedFileCount} changed file{revision.changedFileCount === 1 ? '' : 's'}</p><code>{revision.resultFingerprint ?? 'Result fingerprint not recorded'}</code>{revision.approvedAt && <small>Approved {new Date(revision.approvedAt).toLocaleString()}</small>}</article>)}</details>
            {task.status === 'AwaitingImplementationReview' && persistedCheckoutVerified && activeImplementationRevision?.resultFingerprint && <div className="approval-row implementation-approval-row"><p><strong>Explicit implementation approval required</strong><br />Approval accepts revision {activeImplementationRevision.revisionNumber} and its exact persisted review fingerprint.</p><button type="button" className="primary-button compact" onClick={() => setApprovalDialogOpen(true)} disabled={busy || approvalInFlight}>Approve implementation <span>→</span></button></div>}
            {nativeApprovalDialogSupported
              ? <dialog ref={approvalDialog} className="approval-dialog" onCancel={event => { event.preventDefault(); closeApprovalDialog() }} onClose={() => setApprovalDialogOpen(false)} onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); closeApprovalDialog() } }} aria-labelledby="implementation-approval-title" aria-describedby="implementation-approval-description">{approvalDialogContents}</dialog>
              : approvalDialogOpen && <div className="approval-dialog approval-dialog-fallback" role="dialog" aria-modal="true" aria-labelledby="implementation-approval-title" aria-describedby="implementation-approval-description" onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); closeApprovalDialog() } }}>{approvalDialogContents}</div>}
            <div className="export-actions"><button className="secondary-button" onClick={() => void exportPdf('plan')} disabled={exportingPdf !== null}>Download approved plan</button><button className="secondary-button" onClick={() => void exportPdf('task')} disabled={exportingPdf !== null}>Download task report PDF</button></div>{pdfExportError && <p className="export-error" role="alert">{pdfExportError}</p>}
          </div>}
          {task && hasApprovedPlan && !approvedPlanControlsRenderedInState && <div className="semantic-plan-actions"><div><strong>Approved plan documents</strong><p>The persisted plan approval remains valid in this later workflow state.</p></div><div className="export-actions"><button className="secondary-button" onClick={() => void exportPdf('plan')} disabled={exportingPdf !== null}>Download approved plan</button><button className="secondary-button" onClick={() => void exportPdf('task')} disabled={exportingPdf !== null}>Download task report PDF</button></div></div>}
          {task && task.evidenceItems.length > 0 && <section className="evidence-view"><div className="evidence-heading"><div><p className="eyebrow">SELECTED REPOSITORY EVIDENCE</p><h3>{task.evidenceFilesSelected} files · {task.totalEvidenceCharacters.toLocaleString()} characters</h3></div><span>{task.evidenceFilesInspected} eligible files inspected</span></div>{task.evidenceItems.map(item => <details className="evidence-item" key={item.id}><summary><b>{item.id}</b><code>{item.relativePath}:{item.startLine}-{item.endLine}</code><span>score {item.score}</span></summary><p>{item.reasonSelected}</p><pre>{item.excerpt}</pre></details>)}</section>}
        </section>
        <aside className="context-column">
          <section className="context-card"><div className="aside-title"><span>Repository context</span><i>{task?.repositorySnapshot ? 'READ-ONLY SNAPSHOT' : task ? 'IDENTIFIER ONLY' : 'WAITING'}</i></div>{task ? <><code>{task.repository}</code>{task.repositorySnapshot ? <div className="repository-map"><dl><div><dt>Branch</dt><dd>{task.repositorySnapshot.branch ?? 'n/a'}</dd></div><div><dt>HEAD</dt><dd>{task.repositorySnapshot.shortHeadSha ?? 'n/a'}</dd></div><div><dt>State</dt><dd>{task.repositorySnapshot.workingTreeStatus}</dd></div><div><dt>Files</dt><dd>{task.repositorySnapshot.totalDiscoveredFiles}</dd></div><div><dt>Eligible</dt><dd>{task.repositorySnapshot.eligibleTextFileCount}</dd></div><div><dt>Excluded</dt><dd>{task.repositorySnapshot.excludedFileCount}</dd></div></dl><p><strong>Detected stack</strong><br />{task.repositorySnapshot.detectedLanguages.join(' · ') || 'No supported stack detected'}</p><p><strong>Projects/packages</strong><br />{task.repositorySnapshot.projectFiles.join(', ') || 'None detected'}</p><p><strong>Snapshot freshness</strong><br />{new Date(task.repositorySnapshot.analyzedAt).toLocaleString()}</p>{task.repositorySnapshot.warnings.map(warning => <p className="snapshot-warning" key={warning}>! {warning}</p>)}</div> : <p className="truth-note"><span>!</span> Files are not inspected until requirement approval.</p>}</> : <div className="empty-context"><span>⌘</span><p>Repository details appear after task creation.</p></div>}</section>
          <section className="context-card history-card"><div className="aside-title"><span>Clarification history</span><b>{answeredCount}</b></div>{task && answeredCount > 0 ? task.clarificationAnswers.map((item, index) => <details key={item.answeredAt} open={index === answeredCount - 1}><summary><span>{String(index + 1).padStart(2, '0')}</span>{item.question}</summary><p>{item.answer}</p></details>) : <div className="empty-context compact"><p>Answers are preserved here.</p></div>}</section>
          {task && task.requirementRevisionNotes.length > 0 && <section className="context-card history-card"><div className="aside-title"><span>Summary revisions</span><b>{task.requirementRevisionNotes.length}</b></div>{task.requirementRevisionNotes.map((revision, index) => <details key={revision.submittedAt}><summary><span>R{index + 1}</span>{revision.correction}</summary><p><strong>Previous summary</strong><br />{revision.previousSummary}</p></details>)}</section>}
          <section className="telemetry-card expanded"><div><span>CLARIFICATION CALLS</span><strong>{clarificationCalls.length}</strong></div><div><span>PLANNING CALLS</span><strong>{planningCalls.length}</strong></div><div><span>IMPLEMENTATION CALLS</span><strong>{implementationCalls.length}</strong></div><div><span>INPUT TOKENS</span><strong>{formatTokens(telemetry.totalInputTokens)}</strong></div><div><span>OUTPUT TOKENS</span><strong>{formatTokens(telemetry.totalOutputTokens)}</strong></div><div><span>PLANNING EST. COST</span><strong>${planningCost.toFixed(6)}</strong></div><div><span>TOTAL EST. COST</span><strong>${telemetry.totalEstimatedCostUsd.toFixed(6)}{telemetry.isPartialEstimate ? ' partial' : ''}</strong></div><p><i />{providerLabel}</p>{telemetry.calls.length > 0 && <details className="call-history"><summary>Model call details</summary>{telemetry.calls.map(call => <article key={call.id}><strong>{call.stage} · {call.model}</strong><span className={call.succeeded ? 'call-success' : 'call-failure'}>{call.succeeded ? 'Succeeded' : 'Failed'}</span><small>Reasoning: {call.reasoningEffort} · In {call.inputTokens ?? 'unavailable'} ({call.cachedInputTokens ?? 'unavailable'} cached) · Out {call.outputTokens ?? 'unavailable'} · {call.estimatedCostUsd === null ? 'Cost unavailable' : `Est. $${call.estimatedCostUsd.toFixed(6)}`} · {call.pricingProvenance}</small></article>)}</details>}</section>
        </aside>
      </div>
    </main>
    <footer><span>FORGE AI · BUILD WEEK EDITION</span><p><i />{providerLabel}<b>·</b>Local SQLite persistence</p></footer>
  </div>
}

function formatStatus(status: WorkflowStatus) { return status.replace(/([a-z])([A-Z])/g, '$1 $2') }
function formatTokens(tokens: number) { return tokens.toLocaleString() }
function planningFailureForCategory(category: string | null) {
  if (category === 'missing_direct_evidence') return 'The plan referenced an existing repository file that was not included in the selected evidence.'
  if (category === 'output_truncated') return 'The planning response reached its output limit before the structured plan was complete.'
  if (category === 'content_filter') return "The planning response was stopped by the provider's content filter."
  return 'OpenAI did not return a valid completed planning response.'
}
export default App
