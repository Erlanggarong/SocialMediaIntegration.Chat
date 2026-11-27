// ============================================================
// CrmUtility — helper umum (timestamp X) untuk SocialMediaIntegration.Chat
// Bank Indonesia (BICARA)
//
// Akses CRM on-prem kini lewat CrmWebApi (Web API + NTLM); method query SDK
// (RetrieveWithFilterAndOrder, GetPrecontact) dipindahkan ke sana karena
// tidak lagi memakai IOrganizationService.
// ============================================================

namespace SocialMediaIntegration.Chat;

public static class CrmUtility
{
    /// <summary>
    /// Parse timestamp X API v2 (ISO 8601) secara defensif — port TryParseXTimestamp.
    /// Di sini sumbernya System.Text.Json (bukan Newtonsoft), jadi token berupa
    /// string mentah; DateTimeOffset.TryParse invariant menangani kedua kultur.
    /// </summary>
    public static bool TryParseXTimestamp(string? raw, out DateTime utcTime)
    {
        utcTime = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!DateTimeOffset.TryParse(raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dto))
            return false;

        utcTime = dto.UtcDateTime;
        return true;
    }

    /// <summary>Event chat XDK membawa created_at_msec (epoch millis, UTC).</summary>
    public static DateTime FromEpochMsec(long msec) =>
        DateTimeOffset.FromUnixTimeMilliseconds(msec).UtcDateTime;
}
