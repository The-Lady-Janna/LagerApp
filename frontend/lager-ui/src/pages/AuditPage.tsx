import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useQuery } from '@tanstack/react-query'
import { apiClient } from '../api/client'
import { CollapsibleCard } from '../components/CollapsibleCard'
import { LoadState } from '../components/LoadState'
import { formatDateTime } from '../lib/format'
import { queryKeys } from '../lib/queryKeys'

interface AuditEntryDto {
  id: string
  at: string
  user: string | null
  entityType: string
  entityId: string
  operation: string
  changesJson: string | null
}

const useEntityTypes = () =>
  useQuery({
    queryKey: queryKeys.auditEntityTypes,
    queryFn: async () => (await apiClient.get<string[]>('/audit/entity-types')).data,
  })

const useAudit = (entity: string, id: string, take: number) =>
  useQuery({
    queryKey: queryKeys.auditList(entity, id, take),
    queryFn: async () => {
      const params = new URLSearchParams()
      if (entity) params.set('entity', entity)
      if (id) params.set('id', id)
      params.set('take', String(take))
      return (await apiClient.get<AuditEntryDto[]>(`/audit?${params}`)).data
    },
  })

export function AuditPage() {
  const { t } = useTranslation()
  const types = useEntityTypes()
  const [entity, setEntity] = useState('')
  const [id, setId] = useState('')
  const [take, setTake] = useState(200)
  const audit = useAudit(entity, id, take)

  const formatChanges = (json: string | null): string => {
    if (!json) return ''
    try {
      const parsed = JSON.parse(json)
      return Object.entries(parsed).map(([k, v]) => {
        if (v && typeof v === 'object' && 'old' in (v as object) && 'new' in (v as object)) {
          const vv = v as { old: unknown; new: unknown }
          return `${k}: ${JSON.stringify(vv.old)} → ${JSON.stringify(vv.new)}`
        }
        return `${k}: ${JSON.stringify(v)}`
      }).join('; ')
    } catch {
      return json
    }
  }

  return (
    <>
      <h2>{t('reports:audit.title')}</h2>

      <CollapsibleCard title={t('reports:audit.filter')} storageKey="audit-filter" defaultOpen>
        <div className="grid-3">
          <label>
            {t('reports:audit.entity')}
            <select value={entity} onChange={(e) => setEntity(e.target.value)}>
              <option value="">{t('reports:audit.all')}</option>
              {(types.data ?? []).map((type) => <option key={type} value={type}>{type}</option>)}
            </select>
          </label>
          <label>
            {t('reports:audit.entityId')}
            <input value={id} onChange={(e) => setId(e.target.value)} placeholder={t('reports:audit.entityIdPlaceholder')} />
          </label>
          <label>
            {t('reports:audit.max')}
            <input
              type="number"
              min={1}
              max={1000}
              value={take}
              onChange={(e) => setTake(Math.max(1, Math.min(1000, +e.target.value || 1)))}
            />
          </label>
        </div>
      </CollapsibleCard>

      <CollapsibleCard
        title={audit.data ? t('reports:audit.entriesCount', { count: audit.data.length }) : t('reports:audit.entries')}
        storageKey="audit-list"
        defaultOpen
      >
        {!audit.data && (
          <LoadState isLoading={audit.isLoading} error={audit.error} what={t('reports:audit.what')} onRetry={() => void audit.refetch()} />
        )}
        {audit.data && (
          <table>
            <caption className="visually-hidden">{t('reports:audit.caption')}</caption>
            <thead>
              <tr>
                <th scope="col" style={{ width: 160 }}>{t('reports:audit.col.time')}</th>
                <th scope="col" style={{ width: 120 }}>{t('reports:audit.entity')}</th>
                <th scope="col" style={{ width: 100 }}>{t('reports:audit.col.operation')}</th>
                <th scope="col" style={{ width: 250 }}>{t('reports:audit.entityIdShort')}</th>
                <th scope="col">{t('reports:audit.col.changes')}</th>
                <th scope="col" style={{ width: 100 }}>{t('reports:audit.col.user')}</th>
              </tr>
            </thead>
            <tbody>
              {audit.data.map((a) => (
                <tr key={a.id}>
                  <td style={{ whiteSpace: 'nowrap' }}>{formatDateTime(a.at)}</td>
                  <td>{a.entityType}</td>
                  <td>
                    <span
                      className={a.operation === 'Deleted' ? 'text-danger' : a.operation === 'Added' ? 'text-success' : undefined}
                      style={{ fontWeight: 600, color: a.operation === 'Deleted' || a.operation === 'Added' ? undefined : 'var(--c-info)' }}
                    >{t(`status:auditOperation.${a.operation}`, { defaultValue: a.operation })}</span>
                  </td>
                  <td><code style={{ fontSize: 11 }}>{a.entityId}</code></td>
                  <td style={{ fontSize: 12, fontFamily: 'monospace', wordBreak: 'break-all' }}>
                    {formatChanges(a.changesJson)}
                  </td>
                  <td>{a.user ?? <span className="muted">—</span>}</td>
                </tr>
              ))}
              {audit.data.length === 0 && (
                <tr><td colSpan={6} className="muted">{t('reports:audit.empty')}</td></tr>
              )}
            </tbody>
          </table>
        )}
      </CollapsibleCard>
    </>
  )
}
