import { afterEach, describe, expect, it, vi } from 'vitest'
import { ForgeApiError, forgeApi, parseSafePdfFilename } from './api'

describe('task PDF API helper', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('requests the PDF endpoint and returns its blob and safe filename', async () => {
    const blob = new Blob(['%PDF-1.7'], { type: 'application/pdf' })
    const fetch = vi.fn().mockResolvedValue(new Response(blob, {
      status: 200,
      headers: { 'Content-Disposition': 'attachment; filename="forge-task-abc.pdf"' },
    }))
    vi.stubGlobal('fetch', fetch)

    const result = await forgeApi.exportTaskPdf('abc')

    expect(fetch).toHaveBeenCalledWith('/api/tasks/abc/export/pdf', { headers: { Accept: 'application/pdf' } })
    expect(await result.blob.text()).toBe('%PDF-1.7')
    expect(result.filename).toBe('forge-task-abc.pdf')
  })

  it('rejects unsafe server filenames and uses a deterministic fallback', () => {
    expect(parseSafePdfFilename('attachment; filename="../../bad\r\nname.pdf"', 'safe-id'))
      .toBe('forge-task-safe-id.pdf')
    expect(parseSafePdfFilename("attachment; filename*=UTF-8''forge-task-safe-id.pdf", 'safe-id'))
      .toBe('forge-task-safe-id.pdf')
  })

  it('uses the existing safe problem response behavior', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({
      detail: 'The server could not generate this PDF.', code: 'server_error',
    }), { status: 500, headers: { 'Content-Type': 'application/problem+json' } })))

    await expect(forgeApi.exportTaskPdf('abc')).rejects.toMatchObject({
      message: 'The server could not generate this PDF.', code: 'server_error',
    } satisfies Partial<ForgeApiError>)
  })

  it('lists task summaries and downloads plan PDFs from distinct routes', async () => {
    const fetch = vi.fn()
      .mockResolvedValueOnce(new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(new Blob(['%PDF']), { status: 200,
        headers: { 'Content-Disposition': 'attachment; filename="forge-plan-abc.pdf"' } }))
    vi.stubGlobal('fetch', fetch)

    await expect(forgeApi.listTasks()).resolves.toEqual([])
    await expect(forgeApi.exportPlanPdf('abc')).resolves.toMatchObject({ filename: 'forge-plan-abc.pdf' })
    expect(fetch).toHaveBeenNthCalledWith(2, '/api/tasks/abc/export/plan-pdf', { headers: { Accept: 'application/pdf' } })
    expect(parseSafePdfFilename('attachment; filename="../bad.pdf"', 'abc', 'plan')).toBe('forge-plan-abc.pdf')
  })
})
