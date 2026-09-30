/// <summary>
/// Rollenmodell des Servers (siehe Lager.Domain/Auth/Role.cs und die Policies in
/// Program.cs). Das Frontend ist KEINE Sicherheitsgrenze — der Server bleibt
/// maßgeblich —, bildet aber dieselbe Matrix ab, damit Nutzer nicht in 403-
/// Fehlermeldungen laufen.
/// </summary>
export type RoleName = 'Admin' | 'Manager' | 'Picker' | 'Packer' | 'Receiver' | 'Viewer'

/**
 * Spiegelt die Server-Policies: Admin darf alles, Manager alles außer den
 * Admin-Bereichen, alle anderen Rollen nur ihren eigenen Bereich.
 */
export function hasRequiredRole(userRoles: readonly string[] | undefined, required: RoleName): boolean {
  if (!userRoles) return false
  if (userRoles.includes('Admin')) return true
  if (required === 'Admin') return false
  if (userRoles.includes('Manager')) return true
  return userRoles.includes(required)
}
