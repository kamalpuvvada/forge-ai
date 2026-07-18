import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { RequirementCopyButton } from './RequirementCopyButton'
import { createRequirementCopier, requirementCopyLabel } from './requirementCopy'

describe('approved requirement copying', () => {
  it('copies the complete exact summary as plain text', async () => {
    const write = vi.fn().mockResolvedValue(undefined)
    const copier = createRequirementCopier(write)
    const summary = 'Complete summary\nwith all acceptance criteria.'

    await expect(copier.run(summary)).resolves.toBe(true)
    expect(write).toHaveBeenCalledWith(summary)
  })

  it('prevents duplicate attempts while clipboard writing is pending', async () => {
    let release!: () => void
    const write = vi.fn(() => new Promise<void>(resolve => { release = resolve }))
    const copier = createRequirementCopier(write)
    const first = copier.run('summary')

    await expect(copier.run('summary')).resolves.toBe(false)
    release()
    await expect(first).resolves.toBe(true)
    expect(write).toHaveBeenCalledOnce()
  })

  it('reports absent and rejected clipboard access safely', async () => {
    await expect(createRequirementCopier().run('summary')).rejects.toThrow('Clipboard access is unavailable')
    await expect(createRequirementCopier(() => Promise.reject(new Error('denied'))).run('summary')).rejects.toThrow('denied')
  })

  it('renders a native accessible button with pending and success confirmation labels', () => {
    const idle = renderToStaticMarkup(<RequirementCopyButton state="idle" onCopy={() => undefined} />)
    const pending = renderToStaticMarkup(<RequirementCopyButton state="pending" onCopy={() => undefined} />)
    expect(idle).toContain('<button type="button"')
    expect(idle).toContain('Copy requirement')
    expect(pending).toContain('disabled=""')
    expect(requirementCopyLabel('copied')).toBe('Copied')
    expect(requirementCopyLabel('error')).toBe('Copy requirement')
  })
})
