import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { apiClient } from './client'
import { downloadFile } from '../lib/download'

/// <summary>
/// API-Zugriffe der System-Seite (Backup und Restore, Rolle Admin): eigene Hooks und Typen des Features (api/types.ts und
/// api/hooks.ts bleiben unberührt). Endpunkte: GET/POST api/admin/backups, GET/DELETE api/admin/backups/{name},
/// GET api/admin/backup-settings, POST api/admin/restore (siehe docs/features/backup-restore.md).
/// </summary>

/** Art einer Sicherungsdatei: ein Backup (manuell/Zeitplan) oder die Sicherheitskopie der Live-DB vor einem Restore. */
export type BackupKind = 'backup' | 'before-restore'

export interface BackupFileDto {
  /** Dateiname (nie ein Serverpfad). */
  name: string
  sizeBytes: number
  /** Zeitpunkt der Sicherung, UTC. */
  createdUtc: string
  kind: BackupKind
}

export interface BackupSettingsDto {
  provider: 'Sqlite' | 'MySql'
  /** false bei MySQL und In-Memory-Datenbanken: dort gibt es keine Backups über die Anwendung. */
  supported: boolean
  unsupportedReason: 'mysql' | 'not_file_based' | null
  /** Bei MySQL die Vorlagen für mysqldump bzw. das Einspielen. */
  mysqlDumpCommand: string | null
  mysqlRestoreCommand: string | null
  /** Tägliche Startzeit "HH:mm" (UTC) oder null = kein Zeitplan; bei einem ungültigen Wert der Rohtext. */
  schedule: string | null
  scheduleValid: boolean
  scheduleTimeZone: string
  nextRunUtc: string | null
  /** Wie viele Backups bleiben erhalten; unter 1 = nie automatisch löschen. */
  retentionCount: number
  /** Backup:AllowRestore (gesetzt?) und ob der Restore hier tatsächlich erlaubt ist (Development oder Flag). */
  allowRestore: boolean
  restoreAllowed: boolean
  isDevelopment: boolean
  customDirectory: boolean
  maxRestoreBytes: number
}

export interface RestoreResultDto {
  message: string
  restartRequired: boolean
  sizeBytes: number
  /** Dateiname der Sicherheitskopie der Live-DB vor dem Austausch. */
  safetyBackup: string
}

/** Woher der Restore die Datenbank nimmt: ein vorhandenes Backup (aus der Liste) oder eine hochgeladene Datei. */
export type RestoreSource = { kind: 'backup'; name: string } | { kind: 'upload'; file: File }

/** Der Text, den man zur Bestätigung eines Restores eintippen muss (Formularfeld `confirm`; der Server prüft ihn ebenfalls). */
export const RESTORE_CONFIRMATION = 'RESTORE'

export const systemQueryKeys = {
  backups: ['system', 'backups'] as const,
  backupSettings: ['system', 'backup-settings'] as const,
}

/** Einstellungen und Betriebszustand (Zeitplan, Aufbewahrung, Restore erlaubt, MySQL-Hinweis); nur lesen. */
export const useBackupSettings = () =>
  useQuery({
    queryKey: systemQueryKeys.backupSettings,
    queryFn: async () => (await apiClient.get<BackupSettingsDto>('/admin/backup-settings')).data,
  })

/** Die Sicherungsdateien, neueste zuerst. `enabled` = false bei MySQL (der Endpunkt antwortet dort mit 400). */
export const useBackups = (enabled: boolean) =>
  useQuery({
    queryKey: systemQueryKeys.backups,
    queryFn: async () => (await apiClient.get<BackupFileDto[]>('/admin/backups')).data,
    enabled,
  })

/** Legt jetzt ein Backup an (der Server wendet danach die Aufbewahrung an). */
export const useCreateBackup = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () => (await apiClient.post<BackupFileDto>('/admin/backups')).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: systemQueryKeys.backups }),
  })
}

export const useDeleteBackup = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (name: string) => {
      await apiClient.delete(`/admin/backups/${encodeURIComponent(name)}`)
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: systemQueryKeys.backups }),
  })
}

/**
 * Restore: ein vorhandenes Backup (`backupName`) ODER eine hochgeladene Datei (`file`), immer mit der Bestätigung. Als
 * Multipart-Formular; der Content-Type wird ausdrücklich gesetzt, weil der Client sonst wegen seines JSON-Standards das
 * Formular in JSON umwandeln würde. Danach hat die Liste die neue Sicherheitskopie "before-restore".
 */
export const useRestore = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (source: RestoreSource) => {
      const form = new FormData()
      if (source.kind === 'backup') form.append('backupName', source.name)
      else form.append('file', source.file, source.file.name)
      form.append('confirm', RESTORE_CONFIRMATION)
      return (await apiClient.post<RestoreResultDto>('/admin/restore', form, { headers: { 'Content-Type': 'multipart/form-data' } })).data
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: systemQueryKeys.backups }),
  })
}

/** Lädt ein Backup mit dem Login-Token herunter (ein normaler Link schickt keinen Authorization-Header). */
export const downloadBackup = (name: string): Promise<void> => downloadFile(`/admin/backups/${encodeURIComponent(name)}`, name)
