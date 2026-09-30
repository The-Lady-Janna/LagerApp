using System.Text.Json;

namespace Lager.Tests.WP04;

/// <summary>
/// Reine Frontend-Logik (Token-Ablauf, Rollenmatrix, Passwortwechsel-Vertrag) — die Module aus
/// frontend/lager-ui/src/lib werden dafür direkt mit Node importiert. Ohne passendes Node (>= 22.18)
/// werden die Tests als übersprungen gemeldet.
/// </summary>
public class FrontendLogicTests
{
    [NodeFact]
    public void Session_ist_abgelaufen_sobald_expiresAt_oder_das_JWT_exp_Claim_erreicht_ist()
    {
        using var doc = FrontendFixture.RunNode($$"""
            import { readJwtExpiryMs, getSessionExpiryMs, isSessionExpired } from '{{FrontendFixture.ModuleUrl("src/lib/jwt.ts")}}'
            const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64url')
            const jwt = (payload) => `${b64({ alg: 'HS256' })}.${b64(payload)}.signatur`
            const now = Date.parse('2026-01-01T12:00:00Z')
            const sec = now / 1000
            const iso = (s) => new Date(s * 1000).toISOString()
            const future = sec + 3600, past = sec - 60
            console.log(JSON.stringify({
              readsExp: readJwtExpiryMs(jwt({ exp: future })),
              readsUtf8Payload: readJwtExpiryMs(jwt({ exp: future, name: 'Jörg Müller' })),
              garbage: readJwtExpiryMs('kein-jwt'),
              beideInZukunft: isSessionExpired(jwt({ exp: future }), iso(future), now),
              jwtAbgelaufen: isSessionExpired(jwt({ exp: past }), iso(future), now),
              expiresAtAbgelaufen: isSessionExpired(jwt({ exp: future }), iso(past), now),
              frueherWinnt: getSessionExpiryMs(jwt({ exp: future + 100 }), iso(future)),
              ohneAblaufinfo: getSessionExpiryMs('kein-jwt', null),
              ohneAblaufinfoAbgelaufen: isSessionExpired('kein-jwt', null, now),
              unlesbaresExpiresAt: getSessionExpiryMs(jwt({ exp: future }), 'kein-datum'),
              genauJetzt: isSessionExpired(jwt({ exp: sec }), null, now),
            }))
            """);
        var r = doc.RootElement;
        const double future = 1767268800.0 + 3600; // 2026-01-01T12:00:00Z + 1 h

        Assert.Equal(future * 1000, r.GetProperty("readsExp").GetDouble());
        Assert.Equal(future * 1000, r.GetProperty("readsUtf8Payload").GetDouble());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("garbage").ValueKind);

        Assert.False(r.GetProperty("beideInZukunft").GetBoolean());
        Assert.True(r.GetProperty("jwtAbgelaufen").GetBoolean());        // Token-exp zählt, auch wenn expiresAt noch in der Zukunft liegt
        Assert.True(r.GetProperty("expiresAtAbgelaufen").GetBoolean());
        Assert.Equal(future * 1000, r.GetProperty("frueherWinnt").GetDouble());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("ohneAblaufinfo").ValueKind);
        Assert.False(r.GetProperty("ohneAblaufinfoAbgelaufen").GetBoolean()); // unbekannter Ablauf → der Server entscheidet per 401
        Assert.Equal(future * 1000, r.GetProperty("unlesbaresExpiresAt").GetDouble());
        Assert.True(r.GetProperty("genauJetzt").GetBoolean());
    }

    [NodeFact]
    public void Rollenpruefung_spiegelt_die_Server_Policies()
    {
        using var doc = FrontendFixture.RunNode($$"""
            import { hasRequiredRole } from '{{FrontendFixture.ModuleUrl("src/lib/roles.ts")}}'
            const roles = ['Admin', 'Manager', 'Picker', 'Packer', 'Receiver', 'Viewer']
            const out = {}
            for (const user of [['Admin'], ['Manager'], ['Picker'], ['Viewer'], ['Picker', 'Packer'], []]) {
              out[user.join('+') || 'keine'] = roles.filter((r) => hasRequiredRole(user, r))
            }
            out.undefiniert = roles.filter((r) => hasRequiredRole(undefined, r))
            console.log(JSON.stringify(out))
            """);
        var r = doc.RootElement;
        string[] Granted(string user) => r.GetProperty(user).EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.Equal(new[] { "Admin", "Manager", "Picker", "Packer", "Receiver", "Viewer" }, Granted("Admin"));
        Assert.Equal(new[] { "Manager", "Picker", "Packer", "Receiver", "Viewer" }, Granted("Manager")); // nie Admin-Bereiche
        Assert.Equal(new[] { "Picker" }, Granted("Picker"));
        Assert.Equal(new[] { "Viewer" }, Granted("Viewer"));
        Assert.Equal(new[] { "Picker", "Packer" }, Granted("Picker+Packer"));
        Assert.Empty(Granted("keine"));
        Assert.Empty(Granted("undefiniert"));
    }

    [NodeFact]
    public void Passwortwechsel_Antwort_akzeptiert_204_alt_und_200_mit_Token_neu()
    {
        using var doc = FrontendFixture.RunNode($$"""
            import { parseChangePasswordResponse } from '{{FrontendFixture.ModuleUrl("src/lib/passwordChange.ts")}}'
            const user = { id: 'u1', username: 'lena', roles: ['Picker'], mustChangePassword: false }
            const neu = parseChangePasswordResponse(200, { token: 'abc.def.ghi', expiresAt: '2026-01-01T20:00:00Z', user })
            console.log(JSON.stringify({
              alt204: parseChangePasswordResponse(204, ''),
              neu,
              ohneToken: parseChangePasswordResponse(200, { expiresAt: '2026-01-01T20:00:00Z', user }),
              ohneUser: parseChangePasswordResponse(200, { token: 'abc.def.ghi', expiresAt: '2026-01-01T20:00:00Z' }),
              leererBody: parseChangePasswordResponse(200, ''),
              tokenAberFalscherStatus: parseChangePasswordResponse(204, { token: 'abc.def.ghi', expiresAt: 'x', user }),
            }))
            """);
        var r = doc.RootElement;

        Assert.Equal(JsonValueKind.Null, r.GetProperty("alt204").ValueKind);          // alter Vertrag → neu anmelden
        Assert.Equal("abc.def.ghi", r.GetProperty("neu").GetProperty("token").GetString()); // neuer Vertrag → Session ersetzen
        Assert.Equal("lena", r.GetProperty("neu").GetProperty("user").GetProperty("username").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("ohneToken").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("ohneUser").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("leererBody").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("tokenAberFalscherStatus").ValueKind);
    }
}
