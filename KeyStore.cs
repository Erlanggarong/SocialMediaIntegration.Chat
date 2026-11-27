// ============================================================
// KeyStore — penyimpanan key blob & conversation keys Chat XDK terproteksi DPAPI
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Prinsip keamanan:
//   - Kunci privat dan conversation keys tidak pernah ditulis plaintext ke disk,
//     App.config, registry, maupun git. Satu-satunya bentuk persisten adalah
//     ciphertext DPAPI (DataProtectionScope.CurrentUser).
//   - Field meta hanya memuat data publik (public key SPKI, fingerprint, versi).
//   - Akses blob terbatas pada user Windows yang membuat/memulihkan kunci
//     (DPAPI CurrentUser). Di produksi, jalankan sebagai service account khusus.
// ============================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChatXdk;
using XChat = ChatXdk.Chat; // namespace SocialMediaIntegration.Chat menabrak class ChatXdk.Chat

namespace SocialMediaIntegration.Chat;

/// <summary>Metadata PUBLIK akun chat (aman dibaca siapa pun di mesin).</summary>
public sealed class KeyMeta
{
    [JsonPropertyName("identity_public")] public string IdentityPublic { get; set; } = "";
    [JsonPropertyName("signing_public")] public string SigningPublic { get; set; } = "";
    [JsonPropertyName("public_key_fingerprint")] public string? PublicKeyFingerprint { get; set; }
    /// <summary>Signature atas identity public key (hanya dihasilkan saat generate; disimpan untuk registrasi).</summary>
    [JsonPropertyName("identity_public_key_signature")] public string? IdentityPublicKeySignature { get; set; }
    /// <summary>Signature atas signing public key (kemungkinan null pada versi SDK tertentu).</summary>
    [JsonPropertyName("signing_public_key_signature")] public string? SigningPublicKeySignature { get; set; }
    [JsonPropertyName("registration_method")] public string RegistrationMethod { get; set; } = "CustomPin";
    /// <summary>Versi signing key terdaftar di X API. Null sampai bootstrap registrasi (Fase 2/3) selesai.</summary>
    [JsonPropertyName("signing_key_version")] public string? SigningKeyVersion { get; set; }
    [JsonPropertyName("created_utc")] public DateTime CreatedUtc { get; set; }
    [JsonPropertyName("registered_utc")] public DateTime? RegisteredUtc { get; set; }
}

/// <summary>Blob tersimpan: ciphertext DPAPI + metadata publik.</summary>
public sealed class StoredKeyBlob
{
    [JsonPropertyName("protected_keys_b64")] public string ProtectedKeysB64 { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "CurrentUser";
    [JsonPropertyName("meta")] public KeyMeta Meta { get; set; } = new();
    /// <summary>
    /// Conversation key yang KITA buat saat send (per percakapan, per versi,
    /// base64), diserialisasi sebagai JSON lalu dienkripsi DPAPI CurrentUser.
    /// Wajib disimpan: SDK hanya membungkus key change untuk versi key TERBARU
    /// tiap user — bila akun punya versi lebih baru dari client resmi (HP),
    /// key change buatan API tidak dibungkus untuk key API dan tidak bisa
    /// didekripsi dari event stream. Satu-satunya jalan membaca balik pesan
    /// kita (dan balasan yang dienkripsi dengan key versi kita) adalah key
    /// mentah yang disimpan terproteksi DPAPI di sini.
    /// Struktur plaintext sebelum DPAPI: conversationId kanonik "min:max" -> versi -> key base64.
    /// </summary>
    [JsonPropertyName("protected_conversation_keys_b64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedConversationKeysB64 { get; set; }

    /// <summary>
    /// Format lama (plaintext base64) — hanya dibaca untuk migrasi otomatis ke
    /// ProtectedConversationKeysB64 lalu dihapus saat penyimpanan berikutnya.
    /// </summary>
    [JsonPropertyName("conversation_keys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, Dictionary<string, string>>? LegacyConversationKeys { get; set; }
}

public static class KeyStore
{
    /// <summary>Override direktori store untuk pengujian terisolasi (agar tidak menimpa blob produksi/dev).</summary>
    internal static string? StoreDirectoryOverride { get; set; }

    /// <summary>Folder penyimpanan: %LOCALAPPDATA%\BICARA\SocialMediaIntegration.Chat</summary>
    public static string StoreDirectory =>
        StoreDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BICARA", "SocialMediaIntegration.Chat");

    public static string BlobPath => Path.Combine(StoreDirectory, "chat_keys.dat");

    /// <summary>
    /// Proteksi byte array dengan DPAPI CurrentUser.
    /// </summary>
    public static byte[] Protect(byte[] plaintext)
    {
        if (plaintext is null || plaintext.Length == 0)
            throw new ArgumentException("Data kosong tidak dapat diproteksi.", nameof(plaintext));
        // ProtectedData tersedia di Windows (System.Security.Cryptography.ProtectedData).
        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    /// <summary>Balikkan proteksi DPAPI. Melempar CryptographicException bila blob rusak/di-ekspor lintas user.</summary>
    public static byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);

    /// <summary>
    /// Generate pasangan kunci baru, simpan blob terproteksi + meta publik.
    /// Dipakai oleh bootstrap (Fase 2); tidak menyentuh jaringan.
    ///
    /// PENTING (pelajaran debugging 2026-11-20): identity/signing public key
    /// untuk REGISTRASI harus memakai format dari GenerateKeypairs()
    /// (serialisasi DER/SPKI) — BUKAN dari GetPublicKeys() (raw key point).
    /// identity_public_key_signature ditandatangani atas format SPKI; server
    /// menolak ("invalid request parameters", 400) bila public_key yang
    /// dikirim berformat raw. Bukti: VerifyKeyBinding(SPKI)=true, raw=false.
    /// </summary>
    public static StoredKeyBlob GenerateAndStore()
    {
        using var chat = new XChat();
        var generated = chat.GenerateKeypairs();
        var exported = chat.ExportKeys()
            ?? throw new InvalidOperationException("ExportKeys mengembalikan null.");
        var fp = chat.GetPublicKeyFingerprint();

        var blob = new StoredKeyBlob
        {
            ProtectedKeysB64 = Convert.ToBase64String(Protect(exported)),
            Meta = new KeyMeta
            {
                // Format payload GenerateKeypairs (SPKI) — wajib untuk registrasi.
                IdentityPublic = generated.PublicKey.PublicKey,
                SigningPublic = generated.PublicKey.SigningPublicKey,
                PublicKeyFingerprint = fp,
                IdentityPublicKeySignature = generated.PublicKey.IdentityPublicKeySignature,
                SigningPublicKeySignature = generated.PublicKey.SigningPublicKeySignature,
                RegistrationMethod = generated.PublicKey.RegistrationMethod,
                SigningKeyVersion = null, // diisi setelah POST /2/users/public_keys sukses
                CreatedUtc = DateTime.UtcNow,
            },
        };
        Save(blob);
        return blob;
    }

    /// <summary>
    /// Simpan key privat hasil ekspor (mis. dari Juicebox UnlockAsync) terproteksi DPAPI.
    /// Jika sudah ada blob lama, otomatis di-backup terlebih dahulu dan conversation keys
    /// yang sudah tersimpan tetap dipertahankan.
    /// </summary>
    public static (StoredKeyBlob Blob, string? BackupPath) StoreRecoveredKeys(byte[] exportedKeys, KeyMeta meta)
    {
        if (exportedKeys is null || exportedKeys.Length == 0)
            throw new ArgumentException("Exported keys kosong.", nameof(exportedKeys));

        string? backupPath = null;
        var existing = Load();
        if (existing is not null && File.Exists(BlobPath))
        {
            backupPath = $"{BlobPath}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(BlobPath, backupPath, overwrite: true);
        }

        var blob = new StoredKeyBlob
        {
            ProtectedKeysB64 = Convert.ToBase64String(Protect(exportedKeys)),
            Meta = meta,
            ProtectedConversationKeysB64 = existing?.ProtectedConversationKeysB64,
            LegacyConversationKeys = existing?.LegacyConversationKeys,
        };

        // Jika blob lama masih menyimpan conversation_keys plaintext, ikut migrasikan.
        if (blob.LegacyConversationKeys is { Count: > 0 })
        {
            var all = LoadAllConversationKeys(blob, out _);
            SaveAllConversationKeys(blob, all);
        }
        else
        {
            Save(blob);
        }

        return (blob, backupPath);
    }

    /// <summary>Simpan blob + meta ke disk (ditulis atomik: tmp lalu move overwrite).</summary>
    public static void Save(StoredKeyBlob blob)
    {
        Directory.CreateDirectory(StoreDirectory);
        var tmp = BlobPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(blob, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, BlobPath, overwrite: true);
    }

    public static StoredKeyBlob? Load()
    {
        if (!File.Exists(BlobPath)) return null;
        return JsonSerializer.Deserialize<StoredKeyBlob>(File.ReadAllText(BlobPath));
    }

    /// <summary>Bentuk kanonik id percakapan 1:1: "min:max" (hyphen/colon/bare id diterima).</summary>
    public static string CanonicalConversationId(string conversationId, string? otherUserId = null)
    {
        var parts = conversationId.Split('-', ':');
        if (parts.Length != 2 && otherUserId is not null)
            parts = new[] { conversationId, otherUserId };
        if (parts.Length != 2) return conversationId;
        Array.Sort(parts, StringComparer.Ordinal);
        return parts[0] + ":" + parts[1];
    }

    private static Dictionary<string, Dictionary<string, string>> LoadAllConversationKeys(
        StoredKeyBlob blob, out bool migratedFromLegacy)
    {
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(blob.ProtectedConversationKeysB64))
        {
            var rawJson = Unprotect(Convert.FromBase64String(blob.ProtectedConversationKeysB64));
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(rawJson);
            if (parsed is not null)
            {
                foreach (var (convId, versions) in parsed)
                    all[convId] = new Dictionary<string, string>(versions, StringComparer.Ordinal);
            }
        }

        migratedFromLegacy = false;
        if (blob.LegacyConversationKeys is { Count: > 0 })
        {
            foreach (var (convId, versions) in blob.LegacyConversationKeys)
            {
                if (!all.TryGetValue(convId, out var target))
                    all[convId] = target = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (ver, keyB64) in versions)
                    target.TryAdd(ver, keyB64);
            }
            migratedFromLegacy = true;
        }
        return all;
    }

    private static void SaveAllConversationKeys(
        StoredKeyBlob blob, Dictionary<string, Dictionary<string, string>> all)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all));
        blob.ProtectedConversationKeysB64 = Convert.ToBase64String(Protect(jsonBytes));
        blob.LegacyConversationKeys = null;
        Save(blob);
    }

    /// <summary>Simpan conversation key buatan kita (terproteksi DPAPI, dipanggil saat send).</summary>
    public static void StoreConversationKey(string conversationId, string version, byte[] key)
    {
        var blob = Load() ?? throw new InvalidOperationException("Key blob tidak ditemukan.");
        var canon = CanonicalConversationId(conversationId);
        var all = LoadAllConversationKeys(blob, out _);
        if (!all.TryGetValue(canon, out var byVersion))
            all[canon] = byVersion = new Dictionary<string, string>(StringComparer.Ordinal);
        byVersion[version] = Convert.ToBase64String(key);
        SaveAllConversationKeys(blob, all);
    }

    /// <summary>Ambil semua conversation key tersimpan untuk satu percakapan (versi -> raw bytes).</summary>
    public static IReadOnlyDictionary<string, byte[]> GetConversationKeys(string conversationId)
    {
        var canon = CanonicalConversationId(conversationId);
        var blob = Load();
        if (blob is null)
            return new Dictionary<string, byte[]>();

        var all = LoadAllConversationKeys(blob, out var migrated);
        if (migrated)
        {
            // Migrasi otomatis format plaintext lama ke ciphertext DPAPI.
            SaveAllConversationKeys(blob, all);
        }

        if (!all.TryGetValue(canon, out var byVersion))
            return new Dictionary<string, byte[]>();
        return byVersion.ToDictionary(kv => kv.Key, kv => Convert.FromBase64String(kv.Value));
    }

    /// <summary>
    /// Buka sesi Chat dari blob tersimpan: unprotect DPAPI -> ImportKeys.
    /// signingKeyVersion ikut dipulihkan bila sudah pernah diregistrasi.
    /// </summary>
    public static XChat OpenChat(out KeyMeta meta)
    {
        var blob = Load() ?? throw new InvalidOperationException(
            $"Belum ada key blob di {BlobPath}. Jalankan perintah 'keygen' dulu.");
        var keys = Unprotect(Convert.FromBase64String(blob.ProtectedKeysB64));
        meta = blob.Meta;

        var chat = new XChat();
        var importParams = new ImportKeysParams(keys)
        {
            // Null (belum registrasi) = properti tidak terkirim ke core.
            SigningKeyVersion = meta.SigningKeyVersion,
        };
        chat.ImportKeys(importParams);
        return chat;
    }
}
