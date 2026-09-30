namespace Lager.Domain.Auth;

/// <summary>
/// Coarse-grained roles for the warehouse. Stored as flags so a user can hold
/// several (e.g. a working Lagermeister is Manager+Picker+Packer).
///   - Admin    — User-Verwaltung + Stammdaten + System-Settings
///   - Manager  — Stammdaten + alles operative
///   - Receiver — Wareneingang + Inventur
///   - Picker   — Picklisten abarbeiten
///   - Packer   — Pack-Tisch + Versand
///   - Viewer   — read-only (z. B. Buchhaltung, Geschäftsführung)
/// </summary>
[Flags]
public enum Role
{
    None     = 0,
    Viewer   = 1 << 0,   // 1
    Picker   = 1 << 1,   // 2
    Packer   = 1 << 2,   // 4
    Receiver = 1 << 3,   // 8
    Manager  = 1 << 4,   // 16
    Admin    = 1 << 5,   // 32
}
