export type RequirementCopyState = 'idle' | 'pending' | 'copied' | 'error'

export function requirementCopyLabel(state: RequirementCopyState) {
  if (state === 'pending') return 'Copying…'
  if (state === 'copied') return 'Copied'
  return 'Copy requirement'
}

export function createRequirementCopier(writeText?: (text: string) => Promise<void>) {
  let pending = false
  return {
    get isPending() { return pending },
    async run(text: string) {
      if (pending) return false
      const write = writeText ?? globalThis.navigator?.clipboard?.writeText?.bind(globalThis.navigator.clipboard)
      if (!write) throw new Error('Clipboard access is unavailable in this browser.')
      pending = true
      try { await write(text); return true }
      finally { pending = false }
    },
  }
}
