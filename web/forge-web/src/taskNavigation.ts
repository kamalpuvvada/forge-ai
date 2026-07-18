const taskIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export type TaskSelection =
  | { kind: 'new' }
  | { kind: 'task'; id: string }
  | { kind: 'invalid'; requested: string }

export function parseTaskSelection(search: string): TaskSelection {
  const requested = new URLSearchParams(search).get('task')
  if (requested === null) return { kind: 'new' }
  return taskIdPattern.test(requested)
    ? { kind: 'task', id: requested.toLowerCase() }
    : { kind: 'invalid', requested }
}

export function taskUrl(id: string) { return `/?task=${encodeURIComponent(id.toLowerCase())}` }
export function newTaskUrl() { return '/' }

export interface TaskSelectionToken {
  readonly sequence: number
  readonly taskId: string | null
}

export class TaskSelectionCoordinator {
  private sequence = 0
  private taskId: string | null = null

  begin(taskId: string | null): TaskSelectionToken {
    this.sequence += 1
    this.taskId = taskId
    return this.capture()
  }

  invalidate() { this.sequence += 1 }

  capture(): TaskSelectionToken {
    return { sequence: this.sequence, taskId: this.taskId }
  }

  matches(token: TaskSelectionToken) {
    return token.sequence === this.sequence && token.taskId === this.taskId
  }
}
