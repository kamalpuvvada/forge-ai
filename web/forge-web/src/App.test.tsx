// @vitest-environment jsdom

import { StrictMode } from 'react'
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { taskUrl } from './taskNavigation'
import type { EngineeringTask, EngineeringTaskSummary, ImplementationPlan, SystemCapabilities, WorkflowStatus } from './types'

const apiModule = vi.hoisted(() => {
  class MockForgeApiError extends Error {
    code?: string
    constructor(message: string, code?: string) {
      super(message)
      this.code = code
    }
  }

  return {
    ForgeApiError: MockForgeApiError,
    forgeApi: {
      listTasks: vi.fn(),
      getTask: vi.fn(),
      createTask: vi.fn(),
      answerQuestion: vi.fn(),
      requestRevision: vi.fn(),
      approveRequirement: vi.fn(),
      analyzeRepository: vi.fn(),
      refreshEvidence: vi.fn(),
      createPlan: vi.fn(),
      requestPlanRevision: vi.fn(),
      approvePlan: vi.fn(),
      exportTaskPdf: vi.fn(),
      exportPlanPdf: vi.fn(),
      getCapabilities: vi.fn(),
    },
  }
})

const taskPdfDownloader = vi.hoisted(() => ({ isActive: false, run: vi.fn() }))
const planPdfDownloader = vi.hoisted(() => ({ isActive: false, run: vi.fn() }))
const requirementCopier = vi.hoisted(() => ({ isPending: false, run: vi.fn() }))

vi.mock('./api', () => apiModule)
vi.mock('./pdfDownload', () => ({
  createTaskPdfDownloader: () => taskPdfDownloader,
  createPlanPdfDownloader: () => planPdfDownloader,
  exportErrorMessage: (caught: unknown) => caught instanceof Error ? caught.message : 'The PDF could not be exported.',
}))
vi.mock('./requirementCopy', () => ({
  createRequirementCopier: () => requirementCopier,
  requirementCopyLabel: (state: string) => state === 'pending' ? 'Copying...' : 'Copy summary',
}))

import App from './App'

const { forgeApi } = apiModule
const firstId = '328cbf18-00ca-4fa8-a64e-3a651fb99079'
const secondId = 'b2ec060f-d030-42f0-bffb-bf3e5402ddb2'
const mountedApps: Array<() => Promise<void>> = []
const capabilities: SystemCapabilities = {
  aiMode: 'Fake',
  clarificationProvider: 'Fake',
  clarificationModel: 'demo',
  reasoningEffort: 'low',
  clarificationConfigured: true,
  planningProvider: 'Fake',
  planningModel: 'demo',
  planningReasoningEffort: 'medium',
  planningConfigured: true,
  aiConfigured: true,
  repositoryInspectionAvailable: true,
  planningAvailable: true,
  targetModificationAvailable: false,
  validationAvailable: false,
  reviewAvailable: false,
  pullRequestCreationAvailable: false,
}

describe('App task navigation hardening', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    document.body.innerHTML = ''
    ;(globalThis as typeof globalThis & { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true
    window.history.replaceState(null, '', '/')
    forgeApi.getCapabilities.mockResolvedValue(capabilities)
    forgeApi.listTasks.mockResolvedValue([])
    forgeApi.getTask.mockRejectedValue(new Error('getTask was not configured for this test.'))
    forgeApi.createTask.mockRejectedValue(new Error('createTask was not configured for this test.'))
    forgeApi.answerQuestion.mockRejectedValue(new Error('answerQuestion was not configured for this test.'))
    forgeApi.requestRevision.mockRejectedValue(new Error('requestRevision was not configured for this test.'))
    forgeApi.approveRequirement.mockRejectedValue(new Error('approveRequirement was not configured for this test.'))
    forgeApi.analyzeRepository.mockRejectedValue(new Error('analyzeRepository was not configured for this test.'))
    forgeApi.refreshEvidence.mockRejectedValue(new Error('refreshEvidence was not configured for this test.'))
    forgeApi.createPlan.mockRejectedValue(new Error('createPlan was not configured for this test.'))
    forgeApi.requestPlanRevision.mockRejectedValue(new Error('requestPlanRevision was not configured for this test.'))
    forgeApi.approvePlan.mockRejectedValue(new Error('approvePlan was not configured for this test.'))
    taskPdfDownloader.run.mockResolvedValue(undefined)
    planPdfDownloader.run.mockResolvedValue(undefined)
    requirementCopier.run.mockResolvedValue(undefined)
  })

  afterEach(async () => {
    while (mountedApps.length > 0) {
      await mountedApps.pop()!()
    }
    document.body.innerHTML = ''
  })

  it('ignores a stale task action after navigation back to the new-task page and clears busy state', async () => {
    const task = buildTask(firstId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary A' })
    const approved = buildTask(firstId, 'ReadyForPlanning')
    const pending = deferred<EngineeringTask>()
    forgeApi.getTask.mockResolvedValue(task)
    forgeApi.approveRequirement.mockReturnValue(pending.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Approve requirement')

    await click(findButton(rendered.container, 'Approve requirement'))
    await click(findButton(rendered.container, 'New task'))
    await expectText(rendered.container, 'What are we building?')

    pending.resolve(approved)
    await settle()

    expect(rendered.container.textContent).toContain('What are we building?')
    expect(rendered.container.textContent).not.toContain('Ready for evidence-backed planning')
    expect(findActionCard(rendered.container).getAttribute('aria-busy')).toBe('false')
  })

  it('shows task history and new task navigation for a Clarifying task', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'Clarifying', {
      currentPendingQuestion: 'Which administrator events should we record?',
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(findButton(rendered.container, 'Task history')).toBeTruthy()
    expect(findButton(rendered.container, 'New task')).toBeTruthy()
    expect(rendered.container.textContent).toContain('Save & reevaluate')
  })

  it('shows task history and new task navigation for AwaitingRequirementApproval and keeps approval controls intact', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Approved requirement summary',
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(findButton(rendered.container, 'Task history')).toBeTruthy()
    expect(findButton(rendered.container, 'New task')).toBeTruthy()
    expect(findButton(rendered.container, 'Approve requirement')).toBeTruthy()
    expect(findButton(rendered.container, 'Request correction')).toBeTruthy()
    expect(findButton(rendered.container, 'Copy summary')).toBeTruthy()
  })

  it('shows task history and new task navigation for AwaitingPlanApproval and keeps plan controls intact', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'AwaitingPlanApproval', {
      implementationPlan: buildPlan(),
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(findButton(rendered.container, 'Task history')).toBeTruthy()
    expect(findButton(rendered.container, 'New task')).toBeTruthy()
    expect(findButton(rendered.container, 'Download proposed plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Approve plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Request plan correction')).toBeTruthy()
  })

  it('shows task history and new task navigation for PlanApproved and keeps export controls intact', async () => {
    forgeApi.getTask.mockResolvedValue(buildTask(firstId, 'PlanApproved', {
      implementationPlan: buildPlan(),
      planApprovedAt: '2026-07-18T12:05:00.000Z',
    }))

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    expect(findButton(rendered.container, 'Task history')).toBeTruthy()
    expect(findButton(rendered.container, 'New task')).toBeTruthy()
    expect(findButton(rendered.container, 'Download approved plan')).toBeTruthy()
    expect(findButton(rendered.container, 'Download task report PDF')).toBeTruthy()
  })

  it('keeps task B selected when task A resolves after the user navigates to another task', async () => {
    const taskA = buildTask(firstId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary A', repository: 'C:/repo/A' })
    const taskB = buildTask(secondId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary B', repository: 'C:/repo/B' })
    const approvedA = buildTask(firstId, 'ReadyForPlanning', { repository: 'C:/repo/A' })
    const pending = deferred<EngineeringTask>()
    forgeApi.getTask.mockImplementation((id: string) => Promise.resolve(id === firstId ? taskA : taskB))
    forgeApi.approveRequirement.mockReturnValue(pending.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Approve summary A')

    await click(findButton(rendered.container, 'Approve requirement'))
    await navigate(taskUrl(secondId))
    await expectText(rendered.container, 'Approve summary B')

    pending.resolve(approvedA)
    await settle()

    expect(rendered.container.textContent).toContain('Approve summary B')
    expect(rendered.container.textContent).toContain('C:/repo/B')
    expect(rendered.container.textContent).not.toContain('Ready for evidence-backed planning')
  })

  it('opens task history from a non-approved task, identifies the selected item, switches tasks, updates the URL, and closes the panel', async () => {
    const taskA = buildTask(firstId, 'Clarifying', {
      currentPendingQuestion: 'Which administrator events should we record?',
      repository: 'C:/repo/A',
    })
    const taskB = buildTask(secondId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Approve summary B',
      repository: 'C:/repo/B',
    })
    forgeApi.getTask.mockImplementation((id: string) => Promise.resolve(id === firstId ? taskA : taskB))
    forgeApi.listTasks.mockResolvedValue([
      buildSummary(taskA, 'Recent question'),
      buildSummary(taskB, 'Recent summary'),
    ])

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'Task history'))
    await expectText(rendered.container, 'Recent tasks')
    expect(findButton(rendered.container, 'Task history').getAttribute('aria-expanded')).toBe('true')
    expect(findSelectedHistoryButton(rendered.container).textContent).toContain('C:/repo/A')

    await click(findHistoryButton(rendered.container, 'C:/repo/B'))
    await expectText(rendered.container, 'Approve summary B')

    expect(window.location.search).toBe(`?task=${secondId}`)
    expect(findButton(rendered.container, 'Task history').getAttribute('aria-expanded')).toBe('false')
    expect(rendered.container.textContent).not.toContain('Recent question')
  })

  it('opens a new task from a non-approved task without mutating persisted task data', async () => {
    const task = buildTask(firstId, 'Clarifying', {
      currentPendingQuestion: 'Which administrator events should we record?',
    })
    forgeApi.getTask.mockResolvedValue(task)
    forgeApi.listTasks.mockResolvedValue([buildSummary(task, 'Recent question')])

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'New task'))
    await expectText(rendered.container, 'What are we building?')
    await expectText(rendered.container, 'Recent tasks')

    expect(window.location.pathname).toBe('/')
    expect(window.location.search).toBe('')
    expect(forgeApi.answerQuestion).not.toHaveBeenCalled()
    expect(forgeApi.requestRevision).not.toHaveBeenCalled()
    expect(forgeApi.approveRequirement).not.toHaveBeenCalled()
    expect(forgeApi.analyzeRepository).not.toHaveBeenCalled()
    expect(forgeApi.refreshEvidence).not.toHaveBeenCalled()
    expect(forgeApi.createPlan).not.toHaveBeenCalled()
    expect(forgeApi.requestPlanRevision).not.toHaveBeenCalled()
    expect(forgeApi.approvePlan).not.toHaveBeenCalled()
  })

  it('keeps the current task error when an older selection fails later', async () => {
    const taskA = buildTask(firstId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary A' })
    const taskB = buildTask(secondId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary B' })
    const staleFailure = deferred<EngineeringTask>()
    const currentFailure = deferred<EngineeringTask>()
    forgeApi.getTask.mockImplementation((id: string) => Promise.resolve(id === firstId ? taskA : taskB))
    forgeApi.approveRequirement
      .mockReturnValueOnce(staleFailure.promise)
      .mockReturnValueOnce(currentFailure.promise)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Approve summary A')

    await click(findButton(rendered.container, 'Approve requirement'))
    await navigate(taskUrl(secondId))
    await expectText(rendered.container, 'Approve summary B')
    await click(findButton(rendered.container, 'Approve requirement'))

    currentFailure.reject(new Error('Current task failure'))
    await settle()
    expect(rendered.container.textContent).toContain('Current task failure')

    staleFailure.reject(new Error('Old task failure'))
    await settle()

    expect(rendered.container.textContent).toContain('Current task failure')
    expect(rendered.container.textContent).not.toContain('Old task failure')
  })

  it('still applies the result for the current selection action', async () => {
    const task = buildTask(firstId, 'AwaitingRequirementApproval', { requirementSummary: 'Approve summary A' })
    const approved = buildTask(firstId, 'ReadyForPlanning')
    forgeApi.getTask.mockResolvedValue(task)
    forgeApi.approveRequirement.mockResolvedValue(approved)

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()
    await expectText(rendered.container, 'Approve summary A')

    await click(findButton(rendered.container, 'Approve requirement'))
    await expectText(rendered.container, 'Ready for evidence-backed planning')

    expect(rendered.container.textContent).toContain('Analyze repository and create plan')
  })

  it('preserves Back and Forward task navigation after switching through task history', async () => {
    const taskA = buildTask(firstId, 'Clarifying', {
      currentPendingQuestion: 'Question A',
      repository: 'C:/repo/A',
    })
    const taskB = buildTask(secondId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Approve summary B',
      repository: 'C:/repo/B',
    })
    forgeApi.getTask.mockImplementation((id: string) => Promise.resolve(id === firstId ? taskA : taskB))
    forgeApi.listTasks.mockResolvedValue([
      buildSummary(taskA, 'Question A'),
      buildSummary(taskB, 'Summary B'),
    ])

    await navigate(taskUrl(firstId))
    const rendered = await renderApp()

    await click(findButton(rendered.container, 'Task history'))
    await click(findHistoryButton(rendered.container, 'C:/repo/B'))
    await expectText(rendered.container, 'Approve summary B')

    await dispatchPopState(taskUrl(firstId))
    await expectText(rendered.container, 'Question A')

    await dispatchPopState(taskUrl(secondId))
    await expectText(rendered.container, 'Approve summary B')
  })

  it('restores a deep-linked task safely under StrictMode and removes the popstate listener on cleanup', async () => {
    const staleSelection = buildTask(firstId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Stale summary',
      repository: 'C:/repo/stale',
    })
    const currentSelection = buildTask(firstId, 'AwaitingRequirementApproval', {
      requirementSummary: 'Current summary',
      repository: 'C:/repo/current',
    })
    const capabilityRequests: Array<ReturnType<typeof deferred<SystemCapabilities>>> = []
    const taskRequests: Array<ReturnType<typeof deferred<EngineeringTask>>> = []
    forgeApi.getCapabilities.mockImplementation(() => {
      const request = deferred<SystemCapabilities>()
      capabilityRequests.push(request)
      return request.promise
    })
    forgeApi.getTask.mockImplementation(() => {
      const request = deferred<EngineeringTask>()
      taskRequests.push(request)
      return request.promise
    })

    await navigate(taskUrl(firstId))
    const pushState = vi.spyOn(window.history, 'pushState')
    const rendered = await renderApp(true)

    expect(forgeApi.getCapabilities.mock.calls.length).toBeGreaterThanOrEqual(2)
    expect(forgeApi.getTask.mock.calls.length).toBeGreaterThanOrEqual(2)

    capabilityRequests.at(-1)!.resolve(capabilities)
    taskRequests.at(-1)!.resolve(currentSelection)
    await settle()

    capabilityRequests.slice(0, -1).forEach(request => request.reject(new Error('stale capabilities failure')))
    taskRequests.slice(0, -1).forEach(request => request.resolve(staleSelection))
    await settle()

    expect(rendered.container.textContent).toContain('Current summary')
    expect(rendered.container.textContent).toContain('C:/repo/current')
    expect(rendered.container.textContent).not.toContain('Stale summary')
    expect(rendered.container.textContent).not.toContain('Provider status unavailable')
    expect(pushState).not.toHaveBeenCalled()

    const callsBeforeUnmount = forgeApi.getTask.mock.calls.length
    await rendered.unmount()
    await navigate(taskUrl(secondId))
    expect(forgeApi.getTask).toHaveBeenCalledTimes(callsBeforeUnmount)
  })
})

async function renderApp(strictMode = false) {
  const container = document.createElement('div')
  document.body.appendChild(container)
  const root = createRoot(container)
  await act(async () => {
    root.render(strictMode
      ? <StrictMode><App /></StrictMode>
      : <App />)
  })

  const unmount = async () => {
    const index = mountedApps.indexOf(unmount)
    if (index >= 0) mountedApps.splice(index, 1)
    await act(async () => {
      root.unmount()
    })
    container.remove()
  }
  mountedApps.push(unmount)

  return {
    container,
    unmount,
  }
}

async function click(button: HTMLButtonElement) {
  await act(async () => {
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }))
  })
}

async function navigate(url: string) {
  await act(async () => {
    window.history.pushState(null, '', url)
    window.dispatchEvent(new PopStateEvent('popstate'))
  })
}

async function expectText(container: HTMLElement, text: string) {
  await settle()
  expect(container.textContent).toContain(text)
}

async function settle() {
  await act(async () => {
    await Promise.resolve()
  })
}

async function dispatchPopState(url: string) {
  await act(async () => {
    window.history.replaceState(null, '', url)
    window.dispatchEvent(new PopStateEvent('popstate'))
  })
}

function findButton(container: HTMLElement, label: string) {
  const button = Array.from(container.querySelectorAll('button'))
    .find(candidate => candidate.textContent?.includes(label))
  expect(button).toBeTruthy()
  return button as HTMLButtonElement
}

function findHistoryButton(container: HTMLElement, repository: string) {
  const button = Array.from(container.querySelectorAll('.task-list button'))
    .find(candidate => candidate.textContent?.includes(repository))
  expect(button).toBeTruthy()
  return button as HTMLButtonElement
}

function findSelectedHistoryButton(container: HTMLElement) {
  const button = container.querySelector('.task-list button[aria-current="page"]')
  expect(button).toBeTruthy()
  return button as HTMLButtonElement
}

function findActionCard(container: HTMLElement) {
  const card = container.querySelector('.action-card')
  expect(card).toBeTruthy()
  return card as HTMLElement
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise
    reject = rejectPromise
  })

  return { promise, resolve, reject }
}

function buildTask(id: string, status: WorkflowStatus, overrides: Partial<EngineeringTask> = {}): EngineeringTask {
  const now = '2026-07-18T12:00:00.000Z'
  return {
    id,
    repository: `C:/repo/${id.slice(0, 6)}`,
    originalRequirement: `Requirement ${id}`,
    currentClarifiedRequirement: `Requirement ${id}`,
    clarificationAnswers: [],
    requirementRevisionNotes: [],
    planRevisionNotes: [],
    currentPendingQuestion: null,
    requirementSummary: status === 'AwaitingRequirementApproval' ? `Summary ${id}` : null,
    status,
    createdAt: now,
    updatedAt: now,
    requirementApprovedAt: status === 'ReadyForPlanning' ? now : null,
    planApprovedAt: null,
    repositorySnapshot: null,
    evidenceItems: [],
    evidenceFilesInspected: 0,
    evidenceFilesSelected: 0,
    totalEvidenceCharacters: 0,
    implementationPlan: null,
    repositoryAnalyzedAt: null,
    repositoryFingerprint: null,
    planCreatedAt: null,
    telemetry: {
      totalCalls: 0,
      totalInputTokens: 0,
      totalCachedInputTokens: 0,
      totalOutputTokens: 0,
      totalEstimatedCostUsd: 0,
      costUnavailableCallCount: 0,
      isPartialEstimate: false,
      calls: [],
    },
    ...overrides,
  }
}

function buildSummary(task: EngineeringTask, preview: string): EngineeringTaskSummary {
  return {
    id: task.id,
    status: task.status,
    createdAt: task.createdAt,
    updatedAt: task.updatedAt,
    repository: task.repository,
    originalRequirementPreview: preview,
  }
}

function buildPlan(): ImplementationPlan {
  return {
    title: 'Portable task export plan',
    objective: 'Export task and plan documents.',
    repositoryUnderstanding: 'The repository already has PDF export flow and task history persistence.',
    affectedFiles: [
      {
        path: 'web/forge-web/src/App.tsx',
        action: 'Modify',
        purpose: 'Expose task navigation and download actions.',
        evidenceIds: ['E1'],
        confidence: 0.82,
      },
    ],
    orderedSteps: [
      {
        order: 1,
        description: 'Wire task navigation and PDF entry points into the task screen.',
        affectedPaths: ['web/forge-web/src/App.tsx'],
        evidenceIds: ['E1'],
        expectedResult: 'The screen exposes navigation and export controls without mutating persisted tasks.',
      },
    ],
    proposedValidationCommands: ['npm test -- --run'],
    risks: ['Download state could hide task navigation if not coordinated carefully.'],
    assumptions: ['Persisted task IDs remain the canonical navigation key.'],
    unresolvedQuestions: [],
    requirementCoverage: [
      {
        requirement: 'Provide portable task documentation and navigation.',
        affectedPaths: ['web/forge-web/src/App.tsx'],
        stepOrders: [1],
      },
    ],
    summary: 'The UI exposes navigation and export actions around persisted tasks.',
    source: 'DeterministicFake',
    planningModel: null,
    isDeterministicFake: true,
    createdAt: '2026-07-18T12:00:00.000Z',
    repositoryFingerprint: 'fingerprint',
  }
}
