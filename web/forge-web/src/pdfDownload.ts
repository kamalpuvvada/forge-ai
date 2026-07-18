import { ForgeApiError, forgeApi } from './api'

async function downloadPdf(taskId: string, exportPdf: (id: string) => ReturnType<typeof forgeApi.exportTaskPdf>) {
  const download = await exportPdf(taskId)
  const objectUrl = URL.createObjectURL(download.blob)
  const anchor = document.createElement('a')
  try {
    anchor.href = objectUrl
    anchor.download = download.filename
    anchor.style.display = 'none'
    document.body.appendChild(anchor)
    anchor.click()
  } finally {
    anchor.remove()
    URL.revokeObjectURL(objectUrl)
  }
}

export function downloadTaskPdf(taskId: string) { return downloadPdf(taskId, forgeApi.exportTaskPdf) }
export function downloadPlanPdf(taskId: string) { return downloadPdf(taskId, forgeApi.exportPlanPdf) }

export function createTaskPdfDownloader(download: (taskId: string) => Promise<void> = downloadTaskPdf) {
  let active = false
  return {
    get isActive() { return active },
    async run(taskId: string) {
      if (active) return false
      active = true
      try {
        await download(taskId)
        return true
      } finally {
        active = false
      }
    },
  }
}

export function createPlanPdfDownloader(download: (taskId: string) => Promise<void> = downloadPlanPdf) {
  return createTaskPdfDownloader(download)
}

export function exportErrorMessage(error: unknown) {
  return error instanceof ForgeApiError ? error.message : 'The PDF export could not be completed.'
}
