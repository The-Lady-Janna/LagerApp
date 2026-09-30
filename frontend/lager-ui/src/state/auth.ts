import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import { isSessionExpired } from '../lib/jwt'
import { queryClient } from '../lib/queryClient'
import { hasRequiredRole, type RoleName } from '../lib/roles'
import { useActiveWarehouse } from './activeWarehouse'
import { useToasts } from './toasts'

export interface AuthUser {
  id: string
  username: string
  email: string | null
  displayName: string | null
  roles: string[]
  isActive: boolean
  mustChangePassword: boolean
}

interface AuthState {
  token: string | null
  /** ISO timestamp when the JWT expires. Wird vom Session-Timer für den automatischen Logout genutzt. */
  expiresAt: string | null
  user: AuthUser | null
  /**
   * Der Server hat einen Request mit 403 + code "password_change_required"
   * abgelehnt: bis zum Passwortwechsel ist nur der Passwort-Dialog erreichbar.
   * Nicht persistiert — der nächste Request setzt das Flag bei Bedarf neu.
   */
  passwordChangeRequired: boolean
  /** Hinweis für den Login-Screen (z. B. "Sitzung abgelaufen"). Nicht persistiert. */
  notice: string | null
  setSession: (token: string, expiresAt: string, user: AuthUser) => void
  updateUser: (user: AuthUser) => void
  requirePasswordChange: () => void
  clearPasswordChangeRequired: () => void
  logout: (notice?: string) => void
  hasRole: (role: RoleName) => boolean
}

/// <summary>
/// Persists token + user across reloads (localStorage, Key 'lager.auth').
/// Logout löscht alles: Session, TanStack-Query-Cache und die Lager-Auswahl —
/// so sieht ein nachfolgender User im selben Tab keine Daten des Vorgängers.
/// hasRole spiegelt die Server-Policies (Admin > Manager > Rolle) und ist für
/// UI-Guards gedacht.
/// </summary>
export const useAuth = create<AuthState>()(
  persist(
    (set, get) => ({
      token: null,
      expiresAt: null,
      user: null,
      passwordChangeRequired: false,
      notice: null,
      setSession: (token, expiresAt, user) =>
        set({ token, expiresAt, user, passwordChangeRequired: false, notice: null }),
      updateUser: (user) => set({ user }),
      requirePasswordChange: () => set({ passwordChangeRequired: true }),
      clearPasswordChangeRequired: () => set({ passwordChangeRequired: false }),
      logout: (notice) => {
        set({ token: null, expiresAt: null, user: null, passwordChangeRequired: false, notice: notice ?? null })
        // Laufende Requests des alten Users abbrechen, dann den Cache leeren.
        void queryClient.cancelQueries()
        queryClient.clear()
        useActiveWarehouse.getState().setActive(null)
        // Meldungen des Vorgängers gehören nicht auf den Bildschirm des nächsten Nutzers.
        useToasts.getState().clear()
      },
      hasRole: (role) => hasRequiredRole(get().user?.roles, role),
    }),
    {
      name: 'lager.auth',
      // Nur die Session überlebt einen Reload; Flag und Hinweis nicht.
      partialize: (s) => ({ token: s.token, expiresAt: s.expiresAt, user: s.user }),
    }
  )
)

/** Convenience selector — true if token present AND not expired (expiresAt und JWT-`exp`). */
export function isAuthenticated() {
  const { token, expiresAt } = useAuth.getState()
  if (!token) return false
  return !isSessionExpired(token, expiresAt)
}
