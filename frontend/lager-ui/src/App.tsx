import { lazy, Suspense, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, Route, Routes, useLocation } from 'react-router-dom'
import { apiClient } from './api/client'
import { ArticlesPage } from './pages/ArticlesPage'
import { ArticleEditorPage } from './pages/ArticleEditorPage'
import { StockPage } from './pages/StockPage'
import { OrdersPage } from './pages/OrdersPage'
import { OrderDetailPage } from './pages/OrderDetailPage'
import { PickListsPage } from './pages/PickListsPage'
import { PackingOverviewPage } from './pages/PackingOverviewPage'
import { CartConfigsPage } from './pages/CartConfigsPage'
import { AuditPage } from './pages/AuditPage'
import { ReportsPage } from './pages/ReportsPage'
import { LoginPage } from './pages/LoginPage'
import { NotFoundPage } from './pages/NotFoundPage'
import { UsersPage } from './pages/UsersPage'
import { InboundPage } from './pages/InboundPage'
import { InventoryPage } from './pages/InventoryPage'
import { SuppliersPage } from './pages/SuppliersPage'
import { PurchaseOrdersPage } from './pages/PurchaseOrdersPage'
import { ReturnsPage } from './pages/ReturnsPage'
import { CustomersPage } from './pages/CustomersPage'
import { ShipmentsPage } from './pages/ShipmentsPage'
import { WavesPage } from './pages/WavesPage'
import { ReplenishmentPage } from './pages/ReplenishmentPage'
import { ForcePasswordChange } from './components/ForcePasswordChange'
import { GlobalSearch } from './components/GlobalSearch'
import { RequireRole } from './components/RequireRole'
import { Sidebar } from './components/Sidebar'
import { featureRoutes, type FeatureRoute } from './routes/registry'
import { STATIC_NAV_GROUPS, buildNavGroups } from './routes/navigation'
import { useAuth, isAuthenticated } from './state/auth'

const LayoutEditorPage = lazy(() => import('./pages/LayoutEditorPage').then((m) => ({ default: m.LayoutEditorPage })))
const PickListPage = lazy(() => import('./pages/PickListPage').then((m) => ({ default: m.PickListPage })))
const PackPickListPage = lazy(() => import('./pages/PackPickListPage').then((m) => ({ default: m.PackPickListPage })))
const MobilePickerPage = lazy(() => import('./pages/MobilePickerPage').then((m) => ({ default: m.MobilePickerPage })))

// Sidebar = feste Liste der bestehenden Seiten PLUS die Einträge der Feature-Registry (src/features/*/route.tsx).
// Neue Features ändern diese Datei nicht (Konvention: src/features/README.md).
const NAV_GROUPS = buildNavGroups(STATIC_NAV_GROUPS, featureRoutes)

/** Eine Seite aus der Feature-Registry hinter dem Rollenfilter. */
function FeaturePage({ route }: { route: FeatureRoute }) {
  const Page = route.element
  return <RequireRole roles={route.roles}><Page /></RequireRole>
}

export default function App() {
  const location = useLocation()
  const { t } = useTranslation()
  const token = useAuth((s) => s.token)
  const user = useAuth((s) => s.user)
  const updateUser = useAuth((s) => s.updateUser)
  const passwordChangeRequired = useAuth((s) => s.passwordChangeRequired)

  // Drawer der Sidebar (unter 900 px). Jede Navigation schließt ihn: der Pfad der letzten Darstellung wird mitgeführt und
  // beim Wechsel im selben Render zurückgesetzt (React-Muster "State beim Prop-Wechsel anpassen", kein Effekt nötig). Ein
  // bloßer Vergleich "geöffnet auf Pfad X" würde den Drawer beim Zurück-Navigieren auf X wieder aufklappen.
  const [navOpen, setNavOpen] = useState(false)
  const [drawerPath, setDrawerPath] = useState(location.pathname)
  if (drawerPath !== location.pathname) {
    setDrawerPath(location.pathname)
    setNavOpen(false)
  }
  const toggleRef = useRef<HTMLButtonElement>(null)
  const sidebarRef = useRef<HTMLElement>(null)

  // Bei jedem App-Start mit gültigem Token den User-DTO vom Server frisch
  // holen. Verhindert, dass ein stale `mustChangePassword=true` aus dem
  // localStorage den User in einen Zwangs-Dialog sperrt, obwohl das Passwort
  // längst geändert wurde.
  useEffect(() => {
    if (!token) return
    apiClient.get('/auth/me')
      .then((res) => updateUser(res.data))
      .catch(() => { /* 401 wird vom Axios-Interceptor abgefangen → logout */ })
  }, [token, updateUser])

  // Offener Drawer: Fokus in die Navigation, Escape schließt und gibt den Fokus an den Hamburger-Knopf zurück.
  useEffect(() => {
    if (!navOpen) return
    sidebarRef.current?.querySelector<HTMLElement>('a[href], button, select')?.focus()
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      // Ist ein Dialog offen (z. B. "Passwort ändern" aus der Sidebar), gehört das Escape ihm: sonst würde der Drawer
      // mitschließen und der Fokus vom Dialog in die Kopfleiste springen.
      if (document.querySelector('[aria-modal="true"]')) return
      setNavOpen(false)
      toggleRef.current?.focus()
    }
    document.addEventListener('keydown', onKeyDown)
    return () => document.removeEventListener('keydown', onKeyDown)
  }, [navOpen])

  if (!isAuthenticated()) return <LoginPage />

  // Pflicht-Passwortwechsel (User-DTO oder 403 "password_change_required"):
  // nur der Dialog, keine Oberfläche dahinter.
  if (user?.mustChangePassword === true || passwordChangeRequired) return <ForcePasswordChange />

  const isFullscreenRoute = location.pathname === '/picker' || location.pathname.startsWith('/picker/')

  if (isFullscreenRoute) {
    return (
      <Suspense fallback={<p style={{ padding: 12 }} role="status">{t('common:loading')}</p>}>
        <Routes>
          <Route path="/picker/:id" element={<MobilePickerPage />} />
          <Route path="*" element={<NotFoundPage />} />
        </Routes>
      </Suspense>
    )
  }

  return (
    <div className={navOpen ? 'app app--nav-open' : 'app'}>
      <a className="skip-link" href="#main-content">{t('nav:skipLink')}</a>
      {/* Kopfleiste mit Hamburger: nur unter 900 px sichtbar (index.css), dort ist die Sidebar ein Drawer. */}
      <header className="app-topbar">
        <button
          ref={toggleRef}
          type="button"
          className="nav-toggle"
          aria-label={t('nav:toggle')}
          aria-expanded={navOpen}
          aria-controls="app-sidebar"
          onClick={() => setNavOpen(!navOpen)}
        >
          ☰
        </button>
        <strong>Lager</strong>
      </header>
      <Sidebar id="app-sidebar" ref={sidebarRef} groups={NAV_GROUPS} currentPath={location.pathname} />
      <div className="sidebar-backdrop" onClick={() => setNavOpen(false)} aria-hidden="true" />
      <main id="main-content" className="main" tabIndex={-1}>
        {/* GlobalSearch rendert ein position:fixed-Overlay — JSX-Position ist
            visuell egal, aber als 3. Grid-Kind würde es das Layout brechen.
            Hier drin ist es im "main"-Container und stört die Spalten nicht. */}
        <GlobalSearch />
        <Suspense fallback={<p style={{ padding: 12 }} className="muted" role="status">{t('common:loadingModule')}</p>}>
          <Routes>
            <Route path="/" element={<Navigate to="/articles" replace />} />
            <Route path="/articles" element={<ArticlesPage />} />
            <Route path="/articles/new" element={<ArticleEditorPage />} />
            <Route path="/articles/:id" element={<ArticleEditorPage />} />
            <Route path="/stock" element={<StockPage />} />
            <Route path="/inbound" element={<InboundPage />} />
            <Route path="/inventory" element={<InventoryPage />} />
            <Route path="/inventory/:id" element={<InventoryPage />} />
            <Route path="/orders" element={<OrdersPage />} />
            <Route path="/orders/:id" element={<OrderDetailPage />} />
            <Route path="/picklists" element={<PickListsPage />} />
            <Route path="/picklists/:id" element={<PickListPage />} />
            <Route path="/picklists/:id/pack" element={<PackPickListPage />} />
            <Route path="/packing" element={<PackingOverviewPage />} />
            <Route path="/cart-configs" element={<RequireRole role="Manager"><CartConfigsPage /></RequireRole>} />
            <Route path="/layout" element={<LayoutEditorPage />} />
            <Route path="/reports" element={<ReportsPage />} />
            <Route path="/audit" element={<RequireRole role="Manager"><AuditPage /></RequireRole>} />
            <Route path="/users" element={<RequireRole role="Admin"><UsersPage /></RequireRole>} />
            <Route path="/suppliers" element={<SuppliersPage />} />
            <Route path="/purchase-orders" element={<PurchaseOrdersPage />} />
            <Route path="/returns" element={<ReturnsPage />} />
            <Route path="/customers" element={<CustomersPage />} />
            <Route path="/shipments" element={<ShipmentsPage />} />
            <Route path="/waves" element={<WavesPage />} />
            <Route path="/replenishment" element={<ReplenishmentPage />} />
            {/* Seiten der Feature-Pakete (route.tsx je Ordner unter src/features), Rollenfilter wie bei den festen Seiten. */}
            {featureRoutes.map((route) => (
              <Route key={route.path} path={route.path} element={<FeaturePage route={route} />} />
            ))}
            <Route path="*" element={<NotFoundPage />} />
          </Routes>
        </Suspense>
      </main>
    </div>
  )
}
