import { Component, type ErrorInfo, type ReactNode } from 'react'
import { parseApiError } from '../api/errors'
import { t } from '../i18n'

interface Props { children: ReactNode }
interface State { error: Error | null }

/// <summary>
/// React-ErrorBoundary mit Fallback-UI. Fängt Render-Errors aus Children,
/// damit ein Crash in einer Page nicht die ganze App weiß macht. Zeigt den
/// Fehlertext (bei API-Fehlern die verständliche Meldung samt Referenz/Correlation-Id
/// für den Support) und bietet "Neu laden" an. Logs gehen in die Browser-Konsole —
/// Production-Logging (Sentry o. Ä.) als Folge-Iteration.
/// </summary>
export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('ErrorBoundary fing:', error, info.componentStack)
  }

  render() {
    if (this.state.error) {
      const info = parseApiError(this.state.error, t('errors:boundary.unknown'))
      return (
        <div role="alert" style={{ padding: 24, maxWidth: 700, margin: '40px auto', fontFamily: 'system-ui' }}>
          <h1 style={{ color: 'var(--c-danger-text)' }}>{t('errors:boundary.title')}</h1>
          <p>{t('errors:boundary.body')}</p>
          <pre className="error" style={{ overflow: 'auto', fontSize: 12, whiteSpace: 'pre-wrap' }}>
            {info.message}
            {info.correlationId ? `\n${t('errors:reference', { id: info.correlationId })}` : ''}
          </pre>
          <button className="primary" onClick={() => window.location.reload()} style={{ marginTop: 12 }}>
            {t('common:reload')}
          </button>
          <button onClick={() => this.setState({ error: null })} style={{ marginLeft: 8 }}>
            {t('common:retry')}
          </button>
        </div>
      )
    }
    return this.props.children
  }
}
