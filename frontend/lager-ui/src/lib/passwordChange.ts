import type { AuthUser } from '../state/auth'

/// <summary>
/// Antwort von POST /api/auth/change-password im NEUEN Server-Vertrag:
/// 200 + { token, expiresAt, user } (frisches Token, das alte ist ungültig).
/// Der ALTE Vertrag antwortet mit 204 ohne Body.
/// </summary>
export interface ChangePasswordResult {
  token: string
  expiresAt: string
  user: AuthUser
}

/**
 * Wertet die Antwort beider Vertragsvarianten aus. Liefert die neue Session,
 * wenn der Body ein Token enthält — sonst null (dann muss sich der Nutzer neu
 * anmelden).
 */
export function parseChangePasswordResponse(status: number, data: unknown): ChangePasswordResult | null {
  if (status !== 200 || typeof data !== 'object' || data === null) return null
  const body = data as Partial<ChangePasswordResult>
  if (typeof body.token !== 'string' || body.token.length === 0) return null
  if (typeof body.expiresAt !== 'string' || body.expiresAt.length === 0) return null
  if (typeof body.user !== 'object' || body.user === null) return null
  return { token: body.token, expiresAt: body.expiresAt, user: body.user }
}
