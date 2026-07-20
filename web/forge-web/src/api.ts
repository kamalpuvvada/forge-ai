import type { EngineeringTask, EngineeringTaskSummary, SystemCapabilities } from './types'

interface ProblemDetails { title?: string; detail?: string; code?: string; errors?: Record<string, string[]> }

export class ForgeApiError extends Error {
  readonly code?: string
  constructor(message: string, code?: string) { super(message); this.code = code }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } })
  if (!response.ok) await throwResponseError(response)
  return response.json() as Promise<T>
}

async function throwResponseError(response: Response): Promise<never> {
  let problem: ProblemDetails | undefined
  try { problem = (await response.json()) as ProblemDetails } catch { /* Use the status fallback below. */ }
  const validation = problem?.errors ? Object.values(problem.errors).flat().join(' ') : undefined
  throw new ForgeApiError(validation || problem?.detail || problem?.title || `Request failed (${response.status}).`, problem?.code)
}

export interface TaskPdfDownload { blob: Blob; filename: string }

export function parseSafePdfFilename(contentDisposition: string | null, taskId: string, documentType: 'task' | 'plan' = 'task') {
  const fallback = `forge-${documentType}-${taskId}.pdf`
  if (!contentDisposition) return fallback
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(contentDisposition)?.[1]
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(contentDisposition)?.[1]
  let candidate = encoded ?? plain
  if (!candidate) return fallback
  try { candidate = decodeURIComponent(candidate.trim()) } catch { return fallback }
  return /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\.pdf$/i.test(candidate) ? candidate : fallback
}

export const forgeApi = {
  listTasks: () => request<EngineeringTaskSummary[]>('/api/tasks'),
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
  generateImplementation: (id: string) => request<EngineeringTask>(`/api/tasks/${id}/implementation`, { method: 'POST' }),
  approveImplementation: (id: string, payload: { commandId: string; expectedRowVersion: number; expectedRevisionId: string; expectedResultFingerprint: string }) =>
    request<EngineeringTask>(`/api/tasks/${id}/implementation-approval`, { method: 'POST', body: JSON.stringify(payload) }),
  exportTaskPdf: async (id: string): Promise<TaskPdfDownload> => {
    const response = await fetch(`/api/tasks/${id}/export/pdf`, { headers: { Accept: 'application/pdf' } })
    if (!response.ok) await throwResponseError(response)
    return {
      blob: await response.blob(),
      filename: parseSafePdfFilename(response.headers.get('Content-Disposition'), id),
    }
  },
  exportPlanPdf: async (id: string): Promise<TaskPdfDownload> => {
    const response = await fetch(`/api/tasks/${id}/export/plan-pdf`, { headers: { Accept: 'application/pdf' } })
    if (!response.ok) await throwResponseError(response)
    return {
      blob: await response.blob(),
      filename: parseSafePdfFilename(response.headers.get('Content-Disposition'), id, 'plan'),
    }
  },
  getCapabilities: () => request<SystemCapabilities>('/api/system/capabilities'),
}
