import { afterEach, describe, expect, it, vi } from 'vitest'
import { ForgeApiError, forgeApi } from './api'
import { createPlanPdfDownloader, createTaskPdfDownloader, downloadPlanPdf, downloadTaskPdf, exportErrorMessage } from './pdfDownload'

describe('task PDF browser download', () => {
  afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

  it('clicks a download and revokes the object URL after the attempt', async () => {
    vi.spyOn(forgeApi, 'exportTaskPdf').mockResolvedValue({
      blob: new Blob(['pdf']), filename: 'forge-task-abc.pdf',
    })
    const click = vi.fn()
    const remove = vi.fn()
    const appendChild = vi.fn()
    const anchor = { href: '', download: '', style: { display: '' }, click, remove }
    vi.stubGlobal('document', { createElement: vi.fn(() => anchor), body: { appendChild } })
    const createObjectURL = vi.fn(() => 'blob:forge-pdf')
    const revokeObjectURL = vi.fn()
    vi.stubGlobal('URL', { createObjectURL, revokeObjectURL })

    await downloadTaskPdf('abc')

    expect(anchor.download).toBe('forge-task-abc.pdf')
    expect(click).toHaveBeenCalledOnce()
    expect(remove).toHaveBeenCalledOnce()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:forge-pdf')
  })

  it('prevents duplicate attempts while a download is active', async () => {
    let release!: () => void
    const pending = new Promise<void>(resolve => { release = resolve })
    const download = vi.fn(() => pending)
    const controller = createTaskPdfDownloader(download)

    const first = controller.run('abc')
    const duplicate = await controller.run('abc')
    release()

    expect(duplicate).toBe(false)
    await expect(first).resolves.toBe(true)
    expect(download).toHaveBeenCalledOnce()
  })

  it('returns a safe visible error message', () => {
    expect(exportErrorMessage(new ForgeApiError('The server could not generate this PDF.', 'server_error')))
      .toBe('The server could not generate this PDF.')
    expect(exportErrorMessage(new Error('sensitive browser detail'))).toBe('The PDF export could not be completed.')
    expect(exportErrorMessage({ secret: 'hidden' })).toBe('The PDF export could not be completed.')
  })

  it('downloads the distinct proposed or approved plan PDF and cleans up the object URL', async () => {
    vi.spyOn(forgeApi, 'exportPlanPdf').mockResolvedValue({ blob: new Blob(['plan']), filename: 'forge-plan-abc.pdf' })
    const anchor = { href: '', download: '', style: { display: '' }, click: vi.fn(), remove: vi.fn() }
    vi.stubGlobal('document', { createElement: vi.fn(() => anchor), body: { appendChild: vi.fn() } })
    const revokeObjectURL = vi.fn()
    vi.stubGlobal('URL', { createObjectURL: vi.fn(() => 'blob:plan'), revokeObjectURL })

    await downloadPlanPdf('abc')

    expect(anchor.download).toBe('forge-plan-abc.pdf')
    expect(anchor.click).toHaveBeenCalledOnce()
    expect(revokeObjectURL).toHaveBeenCalledWith('blob:plan')
  })

  it('prevents overlapping plan downloads', async () => {
    let release!: () => void
    const pending = new Promise<void>(resolve => { release = resolve })
    const download = vi.fn(() => pending)
    const controller = createPlanPdfDownloader(download)
    const first = controller.run('abc')

    await expect(controller.run('abc')).resolves.toBe(false)
    release()
    await expect(first).resolves.toBe(true)
    expect(download).toHaveBeenCalledOnce()
  })
})
