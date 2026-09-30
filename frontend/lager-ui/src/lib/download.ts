import { isAxiosError } from 'axios'
import { apiClient } from '../api/client'
import { t } from '../i18n'

/// <summary>
/// Lädt eine geschützte Datei (z. B. Lieferschein-PDF) über den Axios-Client —
/// ein normaler `<a href>` schickt keinen Authorization-Header und bekäme
/// vom abgesicherten Backend ein 401.
/// </summary>
export async function fetchBlob(url: string): Promise<Blob> {
  const res = await apiClient.get<Blob>(url, { responseType: 'blob' })
  return res.data
}

/** Speichert einen Blob per temporärem Link als Datei (Browser-Download). */
export function saveBlob(blob: Blob, filename: string): void {
  const href = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = href
  a.download = filename
  a.rel = 'noreferrer'
  document.body.appendChild(a)
  a.click()
  a.remove()
  // Der Download ist beim Klick bereits gestartet; die URL danach freigeben.
  setTimeout(() => URL.revokeObjectURL(href), 10_000)
}

/** Lädt eine Datei authentifiziert und bietet sie als Download an. */
export async function downloadFile(url: string, filename: string): Promise<void> {
  saveBlob(await fetchBlob(url), filename)
}

/** Kurze Fehlermeldung für einen fehlgeschlagenen Datei-Download. */
export function describeDownloadError(err: unknown): string {
  if (isAxiosError(err)) {
    const status = err.response?.status
    if (status === 404) return t('errors:download.notFound')
    if (status === 403) return t('errors:download.forbidden')
    if (status) return t('errors:download.failedStatus', { status })
    return t('errors:download.offline')
  }
  return t('errors:download.failed')
}
