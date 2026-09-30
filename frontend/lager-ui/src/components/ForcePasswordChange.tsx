import { ChangePasswordDialog } from './ChangePasswordDialog'

/// <summary>
/// Vollbild-Sperre für den Pflicht-Passwortwechsel. Wird von der App-Shell
/// anstelle der normalen Oberfläche gerendert, solange `mustChangePassword`
/// gesetzt ist oder der Server mit 403 "password_change_required" geantwortet
/// hat. So sieht der Nutzer ausschließlich den Dialog (nicht abbrechbar, nur
/// Passwort ändern oder abmelden) und die Seiten dahinter lösen keine Requests
/// aus, die der Server ohnehin ablehnen würde.
/// </summary>
export function ForcePasswordChange() {
  return (
    <div style={{ minHeight: '100dvh', background: 'var(--c-sidebar-bg)' }}>
      {/* onClose ist ein No-op: der Dialog schließt sich nicht selbst, sondern
          verschwindet, sobald die App-Shell den Zustand nicht mehr sperrt. */}
      <ChangePasswordDialog onClose={() => {}} />
    </div>
  )
}
