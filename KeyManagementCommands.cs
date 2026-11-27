// ============================================================
// KeyManagementCommands — bootstrap & pengelolaan kunci E2EE X Chat
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Perintah:
//   keygen        : buat keypair lokal baru & simpan terproteksi DPAPI
//   inspect-keys  : (read-only) cek daftar key di X API & ketersediaan juicebox_config
//   unlock-pin    : pulihkan kunci aplikasi resmi X via Juicebox PIN -> simpan DPAPI
//   register-keys : registrasi public key lokal ke X API (kuota 5 req/24 jam)
// ============================================================

using System.Text;
using System.Text.Json.Nodes;
using ChatXdk;
using XChat = ChatXdk.Chat;

namespace SocialMediaIntegration.Chat;

internal static class KeyManagementCommands
{
    // ---------------- keygen ----------------

    /// <summary>Bootstrap lokal: generate keypair SPKI baru & simpan terproteksi DPAPI.</summary>
    public static int Keygen()
    {
        var blob = KeyStore.GenerateAndStore();
        Console.WriteLine("Key baru digenerate dan disimpan terproteksi DPAPI:");
        Console.WriteLine($"  lokasi           : {KeyStore.BlobPath}");
        Console.WriteLine($"  identity (publik): {blob.Meta.IdentityPublic}");
        Console.WriteLine($"  signing  (publik): {blob.Meta.SigningPublic}");
        Console.WriteLine($"  fingerprint      : {blob.Meta.PublicKeyFingerprint}");
        Console.WriteLine("\nLangkah berikutnya: jalankan 'verify-keys' lalu 'register-keys'.");
        return 0;
    }

    // ---------------- inspect-keys ----------------

    /// <summary>Read-only: cek semua public key terdaftar di akun sendiri & ketersediaan juicebox_config.</summary>
    public static async Task<int> InspectKeysAsync()
    {
        Console.WriteLine("=== inspect-keys: cek public keys & ketersediaan juicebox_config (read-only) ===\n");

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var myUserId = await client.GetAuthenticatedUserIdAsync();
        Console.WriteLine($"Login sebagai user id {myUserId}\n");

        var (resp, usedFieldsParam) = await FetchPublicKeysWithFallbackAsync(client, myUserId);
        Console.WriteLine($"Endpoint: GET /2/users/{myUserId}/public_keys" +
                          (usedFieldsParam ? "?public_key.fields=..." : " (tanpa public_key.fields)"));

        var localMeta = KeyStore.Load()?.Meta;
        var data = resp["data"] as JsonArray;
        Console.WriteLine($"Jumlah key terdaftar di server: {data?.Count ?? 0}\n");

        var foundJuicebox = false;
        if (data is not null)
        {
            var idx = 0;
            foreach (var item in data.OfType<JsonObject>())
            {
                idx++;
                var ver = item["public_key_version"]?.GetValue<string>() ?? "<tanpa versi>";
                var pub = item["public_key"]?.GetValue<string>() ?? "";
                var isLocal = localMeta is not null &&
                              (localMeta.SigningKeyVersion == ver || localMeta.IdentityPublic == pub);
                var jb = item["juicebox_config"];
                if (jb is not null) foundJuicebox = true;

                Console.WriteLine($"  [{idx}] version={ver}{(isLocal ? "  <-- COCOK dengan chat_keys.dat lokal" : "")}");
                Console.WriteLine($"      field tersedia : {string.Join(", ", item.Select(kv => kv.Key))}");
                Console.WriteLine($"      juicebox_config: {(jb is not null ? SummarizeJuicebox(jb) : "TIDAK ADA")}");
            }
        }

        var topJb = resp["juicebox_config"] ?? resp["meta"]?["juicebox_config"];
        if (topJb is not null)
        {
            foundJuicebox = true;
            Console.WriteLine($"\n  [root/meta] juicebox_config: {SummarizeJuicebox(topJb)}");
        }

        if (resp["errors"] is JsonArray errs && errs.Count > 0)
            Console.WriteLine($"\n  [errors dari API]: {errs.ToJsonString()}");

        Console.WriteLine();
        if (foundJuicebox)
        {
            Console.WriteLine("KESIMPULAN: juicebox_config TERSEDIA.");
            Console.WriteLine("Jalankan 'unlock-pin' dengan PIN X Chat akun resmi untuk memakai kunci yang sama dengan aplikasi resmi X.");
        }
        else
        {
            Console.WriteLine("KESIMPULAN: juicebox_config TIDAK dikembalikan X API pada akun/kredensial ini.");
            Console.WriteLine("Gunakan jalur 'keygen' -> 'verify-keys' -> 'register-keys' atau aktifkan PIN X Chat di aplikasi resmi.");
        }

        return 0;
    }

    // ---------------- unlock-pin ----------------

    /// <summary>
    /// Pulihkan kunci privat aplikasi resmi X memakai Juicebox PIN (UnlockAsync),
    /// lalu simpan terproteksi DPAPI ke chat_keys.dat (blob lama otomatis di-backup).
    /// </summary>
    public static async Task<int> UnlockPinAsync()
    {
        Console.WriteLine("=== unlock-pin: pulihkan kunci aplikasi resmi X via Juicebox PIN ===\n");
        Console.WriteLine("PERINGATAN: Juicebox membatasi salah PIN sebelum share kunci dihapus oleh realm.");
        Console.WriteLine("Blob chat_keys.dat lama (jika ada) akan di-backup otomatis sebelum ditimpa.\n");

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var myUserId = await client.GetAuthenticatedUserIdAsync();
        Console.WriteLine($"Login sebagai user id {myUserId}");

        var (resp, _) = await FetchPublicKeysWithFallbackAsync(client, myUserId);
        var records = (resp["data"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();

        JsonNode? jbNode = null;
        JsonObject? jbRecord = null;
        foreach (var r in records)
        {
            if (r["juicebox_config"] is { } candidate)
            {
                jbNode = candidate;
                jbRecord = r;
            }
        }
        jbNode ??= resp["juicebox_config"] ?? resp["meta"]?["juicebox_config"];

        if (jbNode is null)
        {
            Console.Error.WriteLine("\nGAGAL: juicebox_config tidak ditemukan di respons GET /2/users/{id}/public_keys.");
            Console.Error.WriteLine("Jalankan 'inspect-keys' untuk melihat detail respons X API.");
            return 1;
        }

        var juiceboxJson = jbNode is JsonValue val && val.TryGetValue<string>(out var rawStr)
            ? rawStr
            : jbNode.ToJsonString();

        Console.WriteLine($"juicebox_config ditemukan ({SummarizeJuicebox(jbNode)}).");

        Console.Write("\nMasukkan PIN X Chat akun ini (Enter kosong untuk batal): ");
        var pinStr = !Console.IsInputRedirected
            ? XCredentials.ReadSecretLine()
            : Console.ReadLine();
        if (string.IsNullOrEmpty(pinStr))
        {
            Console.WriteLine("Dibatalkan.");
            return 0;
        }

        var pinBytes = Encoding.UTF8.GetBytes(pinStr);
        try
        {
            Console.WriteLine("Membuka kunci dari Juicebox realms...");
            using var chat = new XChat(juiceboxJson);
            await chat.UnlockAsync(pinBytes);

            if (!chat.IsUnlocked)
            {
                Console.Error.WriteLine("GAGAL: sesi tetap terkunci setelah UnlockAsync.");
                return 1;
            }

            var exported = chat.ExportKeys()
                ?? throw new InvalidOperationException("ExportKeys mengembalikan null setelah UnlockAsync.");
            var fp = chat.GetPublicKeyFingerprint();
            Console.WriteLine($"Unlock BERHASIL! Fingerprint kunci: {fp}");

            JsonObject? matched = null;
            foreach (var r in records)
            {
                var pk = r["public_key"]?.GetValue<string>();
                if (pk is not null && SafeMatchesRegisteredKey(chat, pk))
                {
                    matched = r;
                    break;
                }
            }
            matched ??= jbRecord;

            var pubRaw = chat.GetPublicKeys();
            var matchedVersion = matched?["public_key_version"]?.GetValue<string>();
            var meta = new KeyMeta
            {
                IdentityPublic = matched?["public_key"]?.GetValue<string>() ?? pubRaw.Identity,
                SigningPublic = matched?["signing_public_key"]?.GetValue<string>() ?? pubRaw.Signing,
                PublicKeyFingerprint = fp,
                IdentityPublicKeySignature = matched?["identity_public_key_signature"]?.GetValue<string>(),
                SigningPublicKeySignature = matched?["signing_public_key_signature"]?.GetValue<string>(),
                RegistrationMethod = "JuiceboxPin",
                SigningKeyVersion = matchedVersion,
                CreatedUtc = DateTime.UtcNow,
                RegisteredUtc = matchedVersion is not null ? DateTime.UtcNow : null,
            };

            Console.WriteLine(matchedVersion is not null
                ? $"Cocok dengan key terdaftar di server: version {matchedVersion}"
                : "PERINGATAN: public_key_version tidak ditemukan di record server.");

            var (_, backupPath) = KeyStore.StoreRecoveredKeys(exported, meta);
            Array.Clear(exported, 0, exported.Length);

            if (backupPath is not null)
                Console.WriteLine($"Blob lama di-backup ke: {backupPath}");
            Console.WriteLine($"Kunci tersimpan terproteksi DPAPI: {KeyStore.BlobPath}");
            Console.WriteLine("\n=== unlock-pin SELESAI — jalankan 'pull --verbose' untuk verifikasi ===");
            return 0;
        }
        catch (ChatXdkException ex)
        {
            Console.Error.WriteLine($"\nGAGAL UnlockAsync ({ex.Kind}): {ex.Message}");
            if (ex.GuessesRemaining is { } rem)
                Console.Error.WriteLine($"Sisa percobaan PIN di Juicebox: {rem}");
            return 1;
        }
        finally
        {
            Array.Clear(pinBytes, 0, pinBytes.Length);
        }
    }

    // ---------------- register-keys ----------------

    /// <summary>
    /// Registrasi public key lokal ke X API (POST /2/users/{id}/public_keys, kuota 5 req/24 jam).
    /// Melakukan reconcile via GET terlebih dahulu agar tidak membuang kuota jika sudah terdaftar.
    /// </summary>
    public static async Task<int> RegisterKeysAsync()
    {
        Console.WriteLine("=== register-keys: registrasi kunci lokal ke X API ===\n");

        var blob = KeyStore.Load();
        if (blob is null)
        {
            Console.WriteLine("Belum ada key blob — generate baru...");
            blob = KeyStore.GenerateAndStore();
        }
        var meta = blob.Meta;
        Console.WriteLine($"Key lokal: fingerprint {meta.PublicKeyFingerprint}");
        if (meta.SigningKeyVersion is not null)
        {
            Console.WriteLine($"SUDAH terregistrasi (SigningKeyVersion {meta.SigningKeyVersion}, {meta.RegisteredUtc:u}).");
            Console.Write("Registrasi ulang = rotasi kunci. Ketik 'ROTASI' untuk lanjut, Enter untuk batal: ");
            if (Console.ReadLine()?.Trim() != "ROTASI")
            {
                Console.WriteLine("Dibatalkan.");
                return 0;
            }
        }
        if (meta.IdentityPublicKeySignature is null)
            throw new InvalidOperationException(
                "Meta key tidak memiliki signature registrasi — jalankan 'keygen' dulu.");

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var userId = await client.GetAuthenticatedUserIdAsync();
        Console.WriteLine($"Login sebagai user id {userId}");

        var payload = new KeyPairPayload
        {
            Identity = meta.IdentityPublic,
            Signing = meta.SigningPublic,
            IdentityPublicKeySignature = meta.IdentityPublicKeySignature,
            SigningPublicKeySignature = meta.SigningPublicKeySignature,
            PublicKeyFingerprint = meta.PublicKeyFingerprint,
            RegistrationMethod = meta.RegistrationMethod,
        };

        string version;
        var existingKeys = await client.GetPublicKeysAsync(userId);
        var matched = existingKeys
            .OfType<JsonObject>()
            .FirstOrDefault(k => k["public_key"]?.GetValue<string>() == meta.IdentityPublic);
        if (matched is not null)
        {
            version = matched["public_key_version"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"Key terdaftar tapi tanpa version: {matched}");
            Console.WriteLine("Public key sudah terdaftar di akun — POST dilewati (hemat kuota).");
        }
        else
        {
            version = await client.RegisterPublicKeyAsync(userId, payload);
        }

        meta.SigningKeyVersion = version;
        meta.RegisteredUtc = DateTime.UtcNow;
        blob.Meta = meta;
        KeyStore.Save(blob);
        Console.WriteLine($"SigningKeyVersion tersimpan: {version}");
        Console.WriteLine("\n=== register-keys SELESAI ===");
        return 0;
    }

    private static async Task<(JsonNode Response, bool UsedFieldsParam)> FetchPublicKeysWithFallbackAsync(
        XApiChatClient client, string userId)
    {
        try
        {
            var resp = await client.GetPublicKeysFullAsync(userId, requestJuiceboxFields: true);
            return (resp, true);
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode == 400)
        {
            Console.WriteLine("  [info] Parameter public_key.fields ditolak (400), mencoba tanpa query param...");
            var resp = await client.GetPublicKeysFullAsync(userId, requestJuiceboxFields: false);
            return (resp, false);
        }
    }

    private static bool SafeMatchesRegisteredKey(XChat chat, string publicKeyB64)
    {
        try { return chat.MatchesRegisteredKey(publicKeyB64); }
        catch { return false; }
    }

    private static string SummarizeJuicebox(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var keys = obj.Select(kv => kv.Key).ToList();
            var realmCount = obj["realms"] is JsonArray ra ? ra.Count
                : (obj["token_map"] is JsonArray ta ? ta.Count
                : (obj["token_map"] is JsonObject to ? to.Count : (int?)null));
            return $"object ({string.Join(", ", keys)})" + (realmCount is not null ? $" [realms/tokens: {realmCount}]" : "");
        }
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            try
            {
                if (JsonNode.Parse(s) is JsonObject parsed)
                    return "json-string -> " + SummarizeJuicebox(parsed);
            }
            catch { /* bukan json */ }
            return $"string (len {s.Length})";
        }
        return node.GetType().Name;
    }
}
