import type { BackupFileDto, BackupSettingsDto } from '../../api/systemHooks'

/// <summary>Testdaten der System-Seite in der Form der API-DTOs (typisiert gegen api/systemHooks.ts).</summary>

export const NAME_A = 'lager-backup-20260930-020000-000.db'
export const NAME_B = 'lager-before-restore-20260929-101500-250.db'

export function makeSettings(overrides: Partial<BackupSettingsDto> = {}): BackupSettingsDto {
  return {
    provider: 'Sqlite',
    supported: true,
    unsupportedReason: null,
    mysqlDumpCommand: null,
    mysqlRestoreCommand: null,
    schedule: '02:00',
    scheduleValid: true,
    scheduleTimeZone: 'UTC',
    nextRunUtc: '2026-10-01T02:00:00Z',
    retentionCount: 14,
    allowRestore: true,
    restoreAllowed: true,
    isDevelopment: false,
    customDirectory: false,
    maxRestoreBytes: 1024 * 1024 * 1024,
    ...overrides,
  }
}

export function makeBackup(name: string, overrides: Partial<BackupFileDto> = {}): BackupFileDto {
  return { name, sizeBytes: 1536, createdUtc: '2026-09-30T02:00:00Z', kind: 'backup', ...overrides }
}
