import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it } from 'vitest'
import { TaskHistory } from './TaskHistory'
import type { EngineeringTaskSummary } from './types'

const task: EngineeringTaskSummary = {
  id: '328cbf18-00ca-4fa8-a64e-3a651fb99079', status: 'AwaitingPlanApproval',
  createdAt: '2026-07-18T10:00:00Z', updatedAt: '2026-07-18T11:00:00Z',
  repository: 'C:/repo', originalRequirementPreview: 'Add a plan PDF.',
}

describe('recent task history presentation', () => {
  const render = (overrides: Partial<Parameters<typeof TaskHistory>[0]> = {}) => renderToStaticMarkup(<TaskHistory
    tasks={[]} loading={false} error={null} selectedId={null} onSelect={() => undefined} onRetry={() => undefined} {...overrides} />)

  it('shows loading, empty and retryable safe-error states', () => {
    expect(render({ loading: true })).toContain('Loading recent tasks')
    expect(render()).toContain('No persisted tasks yet')
    const error = render({ error: 'Task history is temporarily unavailable.' })
    expect(error).toContain('role="alert"')
    expect(error).toContain('Retry history')
  })

  it('renders accessible selectable history with status, time, repository and preview', () => {
    const html = render({ tasks: [task], selectedId: task.id })
    expect(html).toContain('<button type="button"')
    expect(html).toContain('aria-current="page"')
    expect(html).toContain('Awaiting Plan Approval')
    expect(html).toContain('dateTime="2026-07-18T11:00:00Z"')
    expect(html).toContain('C:/repo')
    expect(html).toContain('Add a plan PDF.')
  })
})
