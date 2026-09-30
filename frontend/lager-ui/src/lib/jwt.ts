/// <summary>
/// Hilfsfunktionen für den Token-Ablauf. Die Signatur wird bewusst NICHT
/// geprüft — das ist Sache des Servers. Das Frontend liest nur das `exp`-Claim,
/// um eine abgelaufene Session früh zu verwerfen (Start, Timer) statt auf das
/// nächste 401 zu warten.
/// </summary>

/** Liest das `exp`-Claim (Sekunden seit Epoch) eines JWT und gibt es in ms zurück; null wenn nicht lesbar. */
export function readJwtExpiryMs(token: string): number | null {
  const parts = token.split('.')
  if (parts.length !== 3) return null
  try {
    const base64 = parts[1].replace(/-/g, '+').replace(/_/g, '/')
    const padded = base64 + '='.repeat((4 - (base64.length % 4)) % 4)
    const bytes = Uint8Array.from(atob(padded), (c) => c.charCodeAt(0))
    const payload: unknown = JSON.parse(new TextDecoder().decode(bytes))
    const exp = (payload as { exp?: unknown } | null)?.exp
    return typeof exp === 'number' && Number.isFinite(exp) ? exp * 1000 : null
  } catch {
    return null
  }
}

/**
 * Ablaufzeitpunkt der Session in ms: der frühere von `expiresAt` (Server-
 * Antwort) und dem `exp`-Claim des Tokens. null = unbekannt (dann entscheidet
 * der Server per 401).
 */
export function getSessionExpiryMs(token: string, expiresAt: string | null): number | null {
  const candidates: number[] = []
  if (expiresAt) {
    const parsed = Date.parse(expiresAt)
    if (Number.isFinite(parsed)) candidates.push(parsed)
  }
  const fromJwt = readJwtExpiryMs(token)
  if (fromJwt !== null) candidates.push(fromJwt)
  return candidates.length > 0 ? Math.min(...candidates) : null
}

/** true, wenn der Ablaufzeitpunkt bekannt und erreicht ist. */
export function isSessionExpired(token: string, expiresAt: string | null, now: number = Date.now()): boolean {
  const expiry = getSessionExpiryMs(token, expiresAt)
  return expiry !== null && expiry <= now
}
