import { describe, expect, it } from 'vitest'
import { getPlanningRecovery, getReadyPlanningAction } from './planningRecovery'

describe('planning recovery UI state', () => {
  it('offers a zero-call evidence refresh for missing direct evidence', () => {
    const recovery = getPlanningRecovery('missing_direct_evidence', false)

    expect(recovery.heading).toBe('Refresh repository evidence before generating another plan.')
    expect(recovery.action).toBe('refresh')
    expect(recovery.note).toContain('makes no model call')
    expect(recovery.note).toContain('separate action')
  })

  it('requires re-analysis when the saved snapshot is stale', () => {
    expect(getPlanningRecovery('missing_direct_evidence', true).action).toBe('reanalyze')
  })

  it('keeps refreshed evidence and plan generation as separate UI actions', () => {
    const ready = getReadyPlanningAction(true)

    expect(ready.eyebrow).toBe('EVIDENCE REFRESHED')
    expect(ready.button).toBe('Generate plan')
    expect(ready.usesSavedEvidence).toBe(true)
  })
})
