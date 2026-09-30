import './i18n'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { QueryClientProvider } from '@tanstack/react-query'
import App from './App'
import './index.css'
import { initTheme } from './state/theme'
import { ErrorBoundary } from './components/ErrorBoundary'
import { ToastHost } from './components/Toast'
import { UpdateBanner } from './components/UpdateBanner'
import { queryClient } from './lib/queryClient'
import { registerServiceWorker } from './lib/serviceWorker'
import { startSessionWatcher } from './lib/sessionWatcher'

// Apply theme BEFORE first render → kein "flash of wrong theme".
initTheme()

// Session-Ablauf überwachen: ein abgelaufenes Token aus dem localStorage wird
// vor dem ersten Render verworfen (→ Login-Screen), ein laufendes loggt beim
// Ablauf automatisch aus.
startSessionWatcher()

// Service-Worker (nur Production) + "Neue Version verfügbar"-Banner.
registerServiceWorker()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </QueryClientProvider>
      <UpdateBanner />
      {/* Globale Fehler-/Hinweis-Toasts (QueryCache/MutationCache.onError, lib/queryClient.ts) — auch außerhalb der App-Shell. */}
      <ToastHost />
    </ErrorBoundary>
  </StrictMode>
)
