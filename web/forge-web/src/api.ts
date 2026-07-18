import type { EngineeringTask, SystemCapabilities } from './types'

interface ProblemDetails { title?: string; detail?: string; code?: string; errors?: Record<string, string[]> }

export class ForgeApiError extends Error {
  readonly code?: string
  constructor(message: string, code?: string) { super(message); this.code = code }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } })
  if (!response.ok) {
    let problem: ProblemDetails | undefined
    try { problem = (await response.json()) as ProblemDetails } catch { /* Use the status fallback below. */ }
    const validation = problem?.errors ? Object.values(problem.errors).flat().join(' ') : undefined
    throw new ForgeApiError(validation || problem?.detail || problem?.title || `Request failed (${response.status}).`, problem?.code)
  }
  return response.json() as Promise<T>
}

export const forgeApi = {
  getTask: (id: string) => request<EngineeringTask>(`/api/tasks/${id}`),
  createTask: (repository: string, requirement: string) => request<EngineeringTask>('/api/tasks', { method: 'POST', body: JSON.stringify({ repository, requirement }) }),
  answerQuestion: (id: string, answer: string) => request<EngineeringTask>(`/api/tasks/${id}/answers`, { method: 'POST', body: JSON.stringify({ answer }) }),
  requestRevision: (id: string, correction: string) => request<EngineeringTask>(`/api/tasks/${id}/requirement-revision`, { method: 'POST', body: JSON.stringify({ correction }) }),
  approveRequirement: (id: string) => request<EngineeringTask>(`/api/tasks/${id}/requirement-approval`, { method: 'POST' }),
  analyzeRepository: (id: string) => request<EngineeringTask>(`/api/tasks/${id}/repository-analysis`, { method: 'POST' }),
  refreshEvidence: (id: string) => request<EngineeringTask>(`/api/tasks/${id}/evidence-refresh`, { method: 'POST' }),
  createPlan: (id: string) => request<EngineeringTask>(`/api/tasks/${id}/plan`, { method: 'POST' }),
  requestPlanRevision: (id: string, correction: string) => request<EngineeringTask>(`/api/tasks/${id}/plan-revision`, { method: 'POST', body: JSON.stringify({ correction }) }),
  approvePlan: (id: string) => request<EngineeringTask>(`/api/tasks/${id}/plan-approval`, { method: 'POST' }),
  getCapabilities: () => request<SystemCapabilities>('/api/system/capabilities'),
}
