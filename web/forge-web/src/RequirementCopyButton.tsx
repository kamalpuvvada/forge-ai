import { requirementCopyLabel } from './requirementCopy'
import type { RequirementCopyState } from './requirementCopy'

export function RequirementCopyButton({ state, onCopy }: { state: RequirementCopyState; onCopy: () => void }) {
  return <button type="button" className="secondary-button copy-button" onClick={onCopy} disabled={state === 'pending'}>
    {requirementCopyLabel(state)}
  </button>
}
