import { ForgeApiError, forgeApi } from './api'

export async function downloadTaskPdf(taskId: string) {
  const download = await forgeApi.exportTaskPdf(taskId)
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

export function exportErrorMessage(error: unknown) {
  return error instanceof ForgeApiError ? error.message : 'The PDF export could not be completed.'
}
