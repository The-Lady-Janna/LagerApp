import { afterEach, describe, expect, it } from 'vitest'
import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AxiosError, AxiosHeaders, type InternalAxiosRequestConfig } from 'axios'
import { parseApiError } from '../../api/errors'
import { ORDER_STATUSES } from '../../api/types'
import { LanguageSwitcher } from '../../components/LanguageSwitcher'
import { StatusPill } from '../../components/StatusPill'
import { OrderStatusPill } from '../../pages/orders/OrderStatusPill'
import { NotFoundPage } from '../../pages/NotFoundPage'
import { RequireRole } from '../../components/RequireRole'
import { renderWithProviders, signInAs } from '../helpers/render'
import { useAuth } from '../../state/auth'
import { DEFAULT_LANGUAGE, LANGUAGE_STORAGE_KEY, i18n, intlLocale, normalizeLanguage, setLanguage } from '../../i18n'
import { describeDays } from '../../features/traceability/expiry'
import { formatDate, formatDateOnly, formatDateTime, formatMoney, formatNumber } from '../../lib/format'
import { featureRoutes } from '../../routes/registry'
import { STATIC_NAV_GROUPS } from '../../routes/navigation'
import { STATUS_TONES } from '../../lib/statusTones'

// WP29: das i18n-Fundament — Standardsprache, Erkennung, Umschalter, <html lang>, Formate, Fehlertexte, Statuswerte, Navigation.

const toEnglish = () => act(() => setLanguage('en'))

afterEach(async () => {
  // setup.ts stellt die Sprache zurück; hier zusätzlich den Browser (Erkennungstests) wieder deutsch machen.
  for (const [name, value] of [['language', 'de-DE'], ['languages', ['de-DE', 'de']]] as const) {
    Object.defineProperty(window.navigator, name, { value, configurable: true })
  }
})

describe('Sprache und <html lang>', () => {
  it('startet deutsch, wechselt auf Englisch (Schlüssel gespeichert, <html lang> folgt) und zurück', async () => {
    expect(DEFAULT_LANGUAGE).toBe('de')
    expect(i18n.language).toBe('de')
    expect(document.documentElement.lang).toBe('de')
    expect(i18n.t('common:cancel')).toBe('Abbrechen')

    await toEnglish()
    expect(document.documentElement.lang).toBe('en')
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en')
    expect(i18n.t('common:cancel')).toBe('Cancel')

    await act(() => setLanguage('de'))
    expect(document.documentElement.lang).toBe('de')
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('de')
  })

  it('normalisiert Sprachcodes: en-US → en, de-AT → de, Unbekanntes → de', () => {
    expect([normalizeLanguage('en-US'), normalizeLanguage('EN_gb'), normalizeLanguage('de-AT'), normalizeLanguage('fr-FR'), normalizeLanguage(undefined)])
      .toEqual(['en', 'en', 'de', 'de', 'de'])
  })

  it('erkennt die Sprache: gespeicherte Wahl → Browsersprache → Deutsch', () => {
    const detect = () => {
      const detector = (i18n.services as unknown as { languageDetector: { detect: () => string | string[] | undefined } }).languageDetector
      const found = detector.detect()
      return normalizeLanguage(Array.isArray(found) ? found[0] : found)
    }
    const setBrowser = (...languages: string[]) => {
      Object.defineProperty(window.navigator, 'languages', { value: languages, configurable: true })
      Object.defineProperty(window.navigator, 'language', { value: languages[0], configurable: true })
    }

    setBrowser('en-US', 'en')
    expect(detect()).toBe('en')                                  // Browser englisch, nichts gespeichert
    localStorage.setItem(LANGUAGE_STORAGE_KEY, 'de')
    expect(detect()).toBe('de')                                  // die gespeicherte Wahl gewinnt
    localStorage.setItem(LANGUAGE_STORAGE_KEY, 'en')
    setBrowser('de-DE', 'de')
    expect(detect()).toBe('en')
    localStorage.clear()
    setBrowser('fr-FR', 'fr')
    expect(detect()).toBe('de')                                  // nicht unterstützt → Deutsch
  })

  it('der Umschalter stellt die Oberfläche um und zeigt die aktive Sprache an', async () => {
    const user = userEvent.setup()
    render(<LanguageSwitcher variant="plain" />)
    const group = screen.getByRole('group', { name: 'Sprache' })
    expect(screen.getByRole('button', { name: 'DE' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('button', { name: 'EN' })).toHaveAttribute('aria-pressed', 'false')

    await user.click(screen.getByRole('button', { name: 'EN' }))
    expect(group).toHaveAttribute('aria-label', 'Language')
    expect(screen.getByRole('button', { name: 'EN' })).toHaveAttribute('aria-pressed', 'true')
    expect(localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('en')
    expect(document.documentElement.lang).toBe('en')
  })
})

describe('Formate folgen der Sprache', () => {
  const normal = (s: string) => s.replace(/\s/g, ' ')

  it('Datum, Uhrzeit, Kalendertag, Zahl und Betrag: Deutsch wie bisher, Englisch (en-GB)', async () => {
    const at = '2026-05-25T10:30:00Z'
    expect(intlLocale()).toBe('de-DE')
    expect(formatDate(at, { timeZone: 'UTC' })).toBe('25.05.2026')
    expect(formatDateTime(at, { timeZone: 'UTC' })).toBe('25.05.2026, 10:30')
    expect(formatDateOnly('2026-05-25')).toBe('25.05.2026')
    expect(formatNumber(1234.5)).toBe('1.234,5')
    expect(normal(formatMoney(123456))).toBe('1.234,56 €')

    await toEnglish()
    expect(intlLocale()).toBe('en-GB')
    expect(formatDate(at, { timeZone: 'UTC' })).toBe('25/05/2026')
    expect(formatDateTime(at, { timeZone: 'UTC' })).toBe('25/05/2026, 10:30')
    expect(formatDateOnly('2026-05-25')).toBe('25/05/2026')
    expect(formatDateOnly('2026-05-25T00:00:00')).toBe('25/05/2026') // kein Zeitzonen-Versatz
    expect(formatNumber(1234.5)).toBe('1,234.5')
    expect(normal(formatMoney(123456))).toBe('€1,234.56')
    expect(formatDateOnly('morgen')).toBe('—')
  })

  it('Fristtexte der MHD-Anzeige werden übersetzt', async () => {
    expect(describeDays(-3)).toBe('seit 3 Tagen abgelaufen')
    await toEnglish()
    expect(describeDays(-3)).toBe('expired 3 days ago')
    expect(describeDays(0)).toBe('expires today')
    expect(describeDays(12)).toBe('12 days left')
  })
})

describe('Fehlertexte (errors-Tabelle)', () => {
  function axiosError(status: number, data?: unknown): AxiosError {
    const config = { headers: new AxiosHeaders() } as InternalAxiosRequestConfig
    return new AxiosError('Request failed', AxiosError.ERR_BAD_RESPONSE, config, {}, { data, status, statusText: '', headers: {}, config })
  }

  it('Deutsch: der Text des Servers hat Vorrang vor der Code-Tabelle (unverändert)', () => {
    const error = axiosError(409, { code: 'insufficient_stock', detail: 'Nicht genug Bestand: 2 vorhanden, 5 benötigt' })
    expect(parseApiError(error).message).toBe('Nicht genug Bestand: 2 vorhanden, 5 benötigt')
  })

  it('Englisch: ein bekannter Code wird übersetzt, ein unbekannter zeigt weiter das deutsche detail', async () => {
    await toEnglish()
    const known = axiosError(409, { code: 'insufficient_stock', detail: 'Nicht genug Bestand: 2 vorhanden, 5 benötigt' })
    expect(parseApiError(known).message).toBe('There is not enough stock for this action.')
    const unknown = axiosError(409, { code: 'something_new', detail: 'Etwas Neues ist passiert.' })
    expect(parseApiError(unknown).message).toBe('Etwas Neues ist passiert.')
  })

  it('Englisch: Statustexte, Netzwerk- und Validierungsmeldungen sind englisch (Feldtexte des Servers bleiben)', async () => {
    await toEnglish()
    expect(parseApiError(axiosError(404)).message).toBe('The record was not found.')
    expect(parseApiError(axiosError(503)).message).toBe('Server error (HTTP 503). Please try again later.')
    expect(parseApiError(new AxiosError('x', AxiosError.ERR_NETWORK)).message).toContain('No connection to the server')
    const validation = axiosError(400, { errors: { Name: ['Name ist erforderlich.'], Sku: ['SKU ist zu lang.'] } })
    expect(parseApiError(validation).message).toBe('Invalid input: Name ist erforderlich. SKU ist zu lang.')
  })
})

describe('Statuswerte', () => {
  it('jeder Status der zentralen Tabelle hat in beiden Sprachen einen Pill-Text; unbekannte erscheinen unverändert', () => {
    for (const [domain, statuses] of Object.entries(STATUS_TONES)) {
      for (const status of Object.keys(statuses)) {
        for (const lng of ['de', 'en']) {
          expect(i18n.getResource(lng, 'status', `pill.${domain}.${status}`), `${lng} pill.${domain}.${status}`).toEqual(expect.any(String))
        }
      }
    }
    render(<StatusPill domain="wave" status="Brandneu" />)
    expect(screen.getByText('Brandneu')).toBeInTheDocument()
  })

  it('Deutsch zeigt die Pills wie bisher (Serverwert), Englisch die lesbare Fassung', async () => {
    const { unmount } = render(<StatusPill domain="purchaseOrder" status="PartiallyReceived" />)
    expect(screen.getByText('PartiallyReceived')).toHaveClass('pill--warning')
    unmount()

    await toEnglish()
    render(<StatusPill domain="purchaseOrder" status="PartiallyReceived" />)
    expect(screen.getByText('Partially received')).toHaveAttribute('data-status', 'PartiallyReceived')
  })

  it('Bestellstatus: deutsche Bezeichnungen, englisch lokalisiert; alle Status haben beide Texte', async () => {
    for (const status of ORDER_STATUSES) {
      for (const lng of ['de', 'en']) expect(i18n.getResource(lng, 'status', `order.${status}`)).toEqual(expect.any(String))
    }
    const { unmount } = render(<OrderStatusPill status="Picking" />)
    expect(screen.getByText('In Kommissionierung')).toBeInTheDocument()
    unmount()

    await toEnglish()
    render(<OrderStatusPill status="Picking" />)
    expect(screen.getByText('Picking')).toHaveAttribute('data-status', 'Picking')
  })
})

describe('Texte mit Auszeichnung (Trans)', () => {
  it('NotFoundPage: die Adresse steht als <code>, in beiden Sprachen', async () => {
    const { unmount } = renderWithProviders(<NotFoundPage />, { initialEntries: ['/nirgends'] })
    expect(screen.getByText('/nirgends').tagName).toBe('CODE')
    expect(screen.getByText(/gibt es nicht/)).toBeInTheDocument()
    unmount()

    await toEnglish()
    renderWithProviders(<NotFoundPage />, { initialEntries: ['/nowhere'] })
    expect(screen.getByText('/nowhere').tagName).toBe('CODE')
    expect(screen.getByText(/does not exist/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Go to start page' })).toHaveAttribute('href', '/')
  })

  it('RequireRole: Rolle hervorgehoben (<strong>), Einzahl und Mehrzahl', async () => {
    signInAs(['Viewer'])
    try {
      const { unmount } = renderWithProviders(<RequireRole role="Manager"><p>geheim</p></RequireRole>)
      expect(screen.getByRole('alert')).toHaveTextContent('Für diese Seite ist die Rolle Manager erforderlich.')
      expect(screen.getByText('Manager').tagName).toBe('STRONG')
      expect(screen.queryByText('geheim')).not.toBeInTheDocument()
      unmount()

      await toEnglish()
      renderWithProviders(<RequireRole roles={['Manager', 'Receiver']}><p>secret</p></RequireRole>)
      expect(screen.getByRole('alert')).toHaveTextContent('This page requires one of the roles Manager or Receiver.')
      expect(screen.getByText('Manager or Receiver').tagName).toBe('STRONG')
      expect(screen.getByRole('heading', { name: 'Access denied' })).toBeInTheDocument()
    } finally {
      useAuth.getState().logout()
    }
  })
})

describe('Navigation', () => {
  const routeKey = (path: string) => path.replace(/^\/+/, '').replace(/\//g, '-')

  it('alle Gruppen und Einträge (fest und aus der Feature-Registry) sind übersetzt; Deutsch entspricht der Quelle', async () => {
    const de = i18n.getFixedT('de')
    const en = i18n.getFixedT('en')
    for (const group of STATIC_NAV_GROUPS) {
      expect(de(`nav:groups.${group.id}`)).toBe(group.label)
      expect(en(`nav:groups.${group.id}`)).not.toBe(`nav:groups.${group.id}`)
      for (const item of group.items) {
        expect(de(`nav:routes.${routeKey(item.to)}`), item.to).toBe(item.label)
        expect(i18n.exists(`nav:routes.${routeKey(item.to)}`, { lng: 'en' }), item.to).toBe(true)
      }
    }
    for (const route of featureRoutes.filter((r) => r.label)) {
      expect(de(`nav:routes.${routeKey(route.path)}`), route.path).toBe(route.label)
      expect(i18n.exists(`nav:routes.${routeKey(route.path)}`, { lng: 'en' }), route.path).toBe(true)
    }
  })
})
