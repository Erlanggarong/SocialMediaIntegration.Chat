// ============================================================
// OAuth1 — penandatanganan OAuth 1.0a (HMAC-SHA1) untuk X API v2
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Port dari pola yang sama dengan plugin CRM (TwitterPostDMPlugin):
// signature base string RFC 5849, tanpa library eksternal.
// Kredensial hanya hidup di memori proses — tidak disimpan ke disk.
// ============================================================

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace SocialMediaIntegration.Chat;

public sealed class XCredentials
{
    public required string ConsumerKey { get; init; }
    public required string ConsumerSecret { get; init; }
    public required string AccessToken { get; init; }
    public required string TokenSecret { get; init; }

    /// <summary>Baca dari environment; yang kosong ditanyakan lewat console (secret tanpa echo).</summary>
    public static XCredentials FromEnvironmentOrPrompt()
    {
        string EnvOrPrompt(string name, string label, bool secret)
        {
            var v = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            Console.Write($"{label}: ");
            var raw = secret && !Console.IsInputRedirected
                ? ReadSecretLine()
                : Console.ReadLine();
            return raw?.Trim()
                ?? throw new InvalidOperationException($"{label} wajib diisi.");
        }
        return new XCredentials
        {
            ConsumerKey = EnvOrPrompt("X_CONSUMER_KEY", "Consumer Key (API Key)", secret: false),
            ConsumerSecret = EnvOrPrompt("X_CONSUMER_SECRET", "Consumer Secret (API Secret)", secret: true),
            AccessToken = EnvOrPrompt("X_ACCESS_TOKEN", "Access Token", secret: false),
            TokenSecret = EnvOrPrompt("X_TOKEN_SECRET", "Access Token Secret", secret: true),
        };
    }

    internal static string ReadSecretLine()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                sb.Append(key.KeyChar);
        }
    }
}

public static class OAuth1
{
    private static string PercentEncode(string value) =>
        Uri.EscapeDataString(value).Replace("+", "%20").Replace("*", "%2A").Replace("%7E", "~");

    /// <summary>
    /// Kirim request yang ditandatangani OAuth 1.0a HMAC-SHA1.
    /// queryParams masuk ke signature (dikenakan juga ke URL bila GET).
    /// body hanya untuk JSON — tidak masuk signature (konten JSON, bukan form).
    /// Setiap percobaan retry menandatangani ulang dengan oauth_nonce &amp;
    /// oauth_timestamp baru agar tidak ditolak replay protection X API.
    /// </summary>
    public static async Task<string> SendSignedAsync(
        HttpClient http, XCredentials cred, HttpMethod method, string url,
        IReadOnlyDictionary<string, string>? queryParams = null, string? jsonBody = null,
        bool retryOn429 = true)
    {
        var uri = new Uri(url);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (queryParams is not null)
            foreach (var kv in queryParams)
                query[kv.Key] = kv.Value;

        using var response = await SendWithRetryAsync(
            http,
            () => BuildSignedRequest(cred, method, uri, query, jsonBody),
            retryOn429);

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"X API {(int)response.StatusCode} ({method} {uri.AbsolutePath}): {body}", null, response.StatusCode);
        return body;
    }

    /// <summary>
    /// Kirim request OAuth 1.0a dan kembalikan respons sebagai byte[] (untuk download media/lampiran).
    /// </summary>
    public static async Task<byte[]> SendSignedBytesAsync(
        HttpClient http, XCredentials cred, HttpMethod method, string url,
        IReadOnlyDictionary<string, string>? queryParams = null, bool retryOn429 = true)
    {
        var uri = new Uri(url);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (queryParams is not null)
            foreach (var kv in queryParams)
                query[kv.Key] = kv.Value;

        using var response = await SendWithRetryAsync(
            http,
            () => BuildSignedRequest(cred, method, uri, query, jsonBody: null),
            retryOn429);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"X API {(int)response.StatusCode}: {err}", null, response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>
    /// Kirim request POST multipart/form-data yang ditandatangani OAuth 1.0a
    /// (untuk append chunk upload media). Sesuai RFC 5849, body multipart tidak masuk signature,
    /// namun queryParams tetap masuk signature base string.
    /// </summary>
    public static async Task<string> SendSignedMultipartAsync(
        HttpClient http, XCredentials cred, string url,
        Func<MultipartFormDataContent> buildContent,
        IReadOnlyDictionary<string, string>? queryParams = null,
        bool retryOn429 = true)
    {
        var uri = new Uri(url);
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (queryParams is not null)
            foreach (var kv in queryParams)
                query[kv.Key] = kv.Value;

        using var response = await SendWithRetryAsync(
            http,
            () =>
            {
                var req = BuildSignedRequest(cred, HttpMethod.Post, uri, query, jsonBody: null);
                req.Content = buildContent();
                return req;
            },
            retryOn429);

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"X API {(int)response.StatusCode} (POST {uri.AbsolutePath}): {body}", null, response.StatusCode);
        return body;
    }

    private static HttpRequestMessage BuildSignedRequest(
        XCredentials cred, HttpMethod method, Uri uri,
        Dictionary<string, string> query, string? jsonBody)
    {
        var oauth = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["oauth_consumer_key"] = cred.ConsumerKey,
            ["oauth_nonce"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ["oauth_token"] = cred.AccessToken,
            ["oauth_version"] = "1.0",
        };

        // Gabungkan semua parameter untuk signature base string.
        var signatureParams = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in query) signatureParams[kv.Key] = kv.Value;
        foreach (var kv in oauth) signatureParams[kv.Key] = kv.Value;

        var paramString = string.Join("&",
            signatureParams.Select(kv => $"{PercentEncode(kv.Key)}={PercentEncode(kv.Value)}"));
        var signatureBase = string.Join("&",
            method.Method.ToUpperInvariant(),
            PercentEncode(uri.GetLeftPart(UriPartial.Path)),
            PercentEncode(paramString));

        var signingKey = $"{PercentEncode(cred.ConsumerSecret)}&{PercentEncode(cred.TokenSecret)}";
        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(signingKey));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(signatureBase)));
        oauth["oauth_signature"] = signature;

        var authHeader = "OAuth " + string.Join(", ",
            oauth.Select(kv => $"{PercentEncode(kv.Key)}=\"{PercentEncode(kv.Value)}\""));

        var fullUrl = uri.GetLeftPart(UriPartial.Path);
        if (query.Count > 0)
            fullUrl += "?" + string.Join("&", query.Select(kv => $"{kv.Key}={PercentEncode(kv.Value)}"));

        var request = new HttpRequestMessage(method, fullUrl);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authHeader);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        return request;
    }

    /// <summary>
    /// Kirim request dengan retry terkendali untuk 429 (Too Many Requests)
    /// dan kegagalan 5xx sementara. Menghormati header Retry-After (detik);
    /// tanpa header memakai backoff eksponensial 2s -> 4s -> 8s (max 3 retry).
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient http, Func<HttpRequestMessage> buildRequest, bool retryOn429)
    {
        const int maxRetries = 3;
        for (var attempt = 0; ; attempt++)
        {
            using var attemptRequest = buildRequest();
            var response = await http.SendAsync(attemptRequest);

            var statusCode = response.StatusCode;
            var retryable = (retryOn429 && statusCode == System.Net.HttpStatusCode.TooManyRequests)
                || ((int)statusCode >= 500 && (int)statusCode < 600);
            if (!retryable || attempt >= maxRetries)
            {
                if (statusCode == System.Net.HttpStatusCode.TooManyRequests && !retryOn429)
                {
                    // Endpoint berkuota harian kecil (mis. POST public_keys = 5/24jam):
                    // jangan pernah retry — setiap percobaan membakar kuota.
                    Console.Error.WriteLine(
                        "  [429] Kuota harian endpoint ini kemungkinan habis (POST public_keys: 5 request/24 jam per user). " +
                        "Tidak di-retry sengaja. Coba lagi setelah jendela 24 jam berlalu.");
                }
                return response;
            }

            // Baca seluruh header SEBELUM Dispose() respons.
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 2s, 4s, 8s
            if (response.Headers.RetryAfter?.Delta is { } delta)
                delay = delta;
            else if (response.Headers.RetryAfter?.Date is { } date)
                delay = date - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            if (delay > TimeSpan.FromSeconds(120)) delay = TimeSpan.FromSeconds(120);

            // Diagnostik: X mengirim quota & waktu reset di header response.
            var remaining = response.Headers.TryGetValues("x-rate-limit-remaining", out var rem)
                ? string.Join(",", rem) : "?";
            // Endpoint key-management (public_keys) memakai header khusus 24 jam.
            string? userReset = null;
            if (response.Headers.TryGetValues("x-user-limit-24hour-reset", out var ur)
                && long.TryParse(string.Join("", ur), out var urEpoch))
            {
                var urAt = DateTimeOffset.FromUnixTimeSeconds(urEpoch).ToLocalTime();
                userReset = $"reset kuota 24 jam: pukul {urAt:HH:mm:ss} ({Math.Max(0, (urAt - DateTimeOffset.Now).TotalMinutes):0} menit lagi)";
            }
            string resetInfo;
            if (response.Headers.TryGetValues("x-rate-limit-reset", out var rst)
                && long.TryParse(string.Join("", rst), out var resetEpoch))
            {
                var resetAt = DateTimeOffset.FromUnixTimeSeconds(resetEpoch).ToLocalTime();
                resetInfo = $"reset window: pukul {resetAt:HH:mm:ss} ({Math.Max(0, (resetAt - DateTimeOffset.Now).TotalMinutes):0} menit lagi)";
            }
            else
            {
                resetInfo = "header reset tidak ada";
            }
            if (userReset is not null)
                resetInfo += " | " + userReset;

            response.Dispose();

            Console.Error.WriteLine(
                $"  [retry {attempt + 1}/{maxRetries}] {(int)statusCode} — sisa kuota {remaining}, {resetInfo}, tunggu {delay.TotalSeconds:0}s...");
            await Task.Delay(delay);
        }
    }
}
