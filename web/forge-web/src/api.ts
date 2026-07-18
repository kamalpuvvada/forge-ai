import type { EngineeringTask } from './types'

interface ProblemDetails { title?: string; detail?: string; errors?: Record<string, string[]> }

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } })
  if (!response.ok) {
    let problem: ProblemDetails | undefined
    try { problem = (await response.json()) as ProblemDetails } catch { /* Use the status fallback below. */ }
    const validation = problem?.errors ? Object.values(problem.errors).flat().join(' ') : undefined
    throw new Error(validation || problem?.detail || problem?.title || `Request failed (${response.status}).`)
  }
  return response.json() as Promise<T>
}

export const forgeApi = {
  createTask: (repository: string, requirement: string) => request<EngineeringTask>('/api/tasks', { method: 'POST', body: JSON.stringify({ repository, requirement }) }),
  answerQuestion: (id: string, answer: string) => request<EngineeringTask>(`/api/tasks/${id}/answers`, { method: 'POST', body: JSON.stringify({ answer }) }),
  approveRequirement: (id: string) => request<EngineeringTask>(`/api/tasks/${id}/requirement-approval`, { method: 'POST' }),
}
