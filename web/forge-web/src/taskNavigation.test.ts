import { describe, expect, it } from 'vitest'
import { TaskSelectionCoordinator, newTaskUrl, parseTaskSelection, taskUrl } from './taskNavigation'

const firstId = '328cbf18-00ca-4fa8-a64e-3a651fb99079'
const secondId = 'b2ec060f-d030-42f0-bffb-bf3e5402ddb2'

describe('native task navigation', () => {
  it('uses one canonical URL for selection, startup, refresh remount and new-task return', () => {
    expect(taskUrl(firstId.toUpperCase())).toBe(`/?task=${firstId}`)
    expect(parseTaskSelection(`?task=${firstId}`)).toEqual({ kind: 'task', id: firstId })
    expect(parseTaskSelection(`?task=${firstId}`)).toEqual(parseTaskSelection(`?task=${firstId}`))
    expect(newTaskUrl()).toBe('/')
    expect(parseTaskSelection('')).toEqual({ kind: 'new' })
  })

  it('represents invalid IDs without replacing the requested selection', () => {
    expect(parseTaskSelection('?task=not-a-guid')).toEqual({ kind: 'invalid', requested: 'not-a-guid' })
    expect(parseTaskSelection('?task=')).toEqual({ kind: 'invalid', requested: '' })
  })

  it('treats any well-formed API Guid as loadable even when it is missing', () => {
    expect(parseTaskSelection('?task=00000000-0000-0000-0000-000000000000'))
      .toEqual({ kind: 'task', id: '00000000-0000-0000-0000-000000000000' })
  })

  it('supports back and forward selections through repeated location parsing', () => {
    expect(parseTaskSelection(`?task=${firstId}`)).toMatchObject({ id: firstId })
    expect(parseTaskSelection('')).toEqual({ kind: 'new' })
    expect(parseTaskSelection(`?task=${secondId}`)).toMatchObject({ id: secondId })
    expect(parseTaskSelection(`?task=${firstId}`)).toMatchObject({ id: firstId })
  })

  it('invalidates prior task actions and deep-link loads when the canonical selection changes', () => {
    const coordinator = new TaskSelectionCoordinator()
    const taskA = coordinator.begin(firstId)
    expect(coordinator.matches(taskA)).toBe(true)

    const taskB = coordinator.begin(secondId)
    expect(coordinator.matches(taskA)).toBe(false)
    expect(coordinator.matches(taskB)).toBe(true)

    const newTask = coordinator.begin(null)
    expect(coordinator.matches(taskB)).toBe(false)
    expect(coordinator.matches(newTask)).toBe(true)

    coordinator.invalidate()
    expect(coordinator.matches(newTask)).toBe(false)
  })

  it('captures the current task identity for task-scoped workflows', () => {
    const coordinator = new TaskSelectionCoordinator()
    coordinator.begin(firstId)

    const active = coordinator.capture()
    expect(active.taskId).toBe(firstId)
    expect(coordinator.matches(active)).toBe(true)

    coordinator.begin(firstId)
    expect(coordinator.matches(active)).toBe(false)
  })
})
