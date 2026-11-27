// ============================================================
// DiagnosticsCommands — pengujian & validasi lokal (offline)
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Perintah:
//   check-native   (alias: fase1) -> uji load native chat_xdk_ffi.dll & dekripsi fixture
//   check-keystore (alias: fase2) -> uji DPAPI & ConversationKeys (terisolasi di %TEMP%)
//   verify-keys                   -> uji konsistensi kriptografi chat_keys.dat lokal
// ============================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ChatXdk;
using XChat = ChatXdk.Chat;

namespace SocialMediaIntegration.Chat;

internal static class DiagnosticsCommands
{
    private sealed class SdkVectors
    {
        [JsonPropertyName("private_keys_concat_b64")] public string PrivateKeysConcatB64 { get; init; } = "";
        [JsonPropertyName("identity_public_b64")] public string IdentityPublicB64 { get; init; } = "";
        [JsonPropertyName("signing_public_b64")] public string SigningPublicB64 { get; init; } = "";
        [JsonPropertyName("identity_public_key_signature_b64")] public string IdentityPublicKeySignatureB64 { get; init; } = "";
        [JsonPropertyName("conversation_key_b64")] public string ConversationKeyB64 { get; init; } = "";
        [JsonPropertyName("event_conversation_id")] public string EventConversationId { get; init; } = "";
        [JsonPropertyName("event_conversation_key_version")] public string EventConversationKeyVersion { get; init; } = "";
        [JsonPropertyName("event_message_b64")] public string EventMessageB64 { get; init; } = "";
        [JsonPropertyName("event_message_text")] public string EventMessageText { get; init; } = "";
        [JsonPropertyName("event_sender_id")] public string EventSenderId { get; init; } = "";
        [JsonPropertyName("event_signing_key_version")] public string EventSigningKeyVersion { get; init; } = "";
        [JsonPropertyName("event_conversation_key_media_b64")] public string EventConversationKeyMediaB64 { get; init; } = "";
        [JsonPropertyName("media_conversation_key_hash_key")] public string MediaConversationKeyHashKey { get; init; } = "";
        [JsonPropertyName("media_conversation_key_encrypted_b64")] public string MediaConversationKeyEncryptedB64 { get; init; } = "";
        [JsonPropertyName("media_conversation_key_plaintext_b64")] public string MediaConversationKeyPlaintextB64 { get; init; } = "";
    }

    // ---------------- check-native (alias: fase1) ----------------

    /// <summary>
    /// Uji offline native library chat_xdk_ffi.dll: load DLL, round-trip kunci 64B,
    /// dan dekripsi event Message dari test vector resmi (fixtures/sdk_vectors.json).
    /// </summary>
    public static int CheckNative()
    {
        Console.WriteLine("=== check-native: uji native chat_xdk_ffi.dll & kripto dasar ===\n");
        var step = 0;

        void Ok(string msg) => Console.WriteLine($"  OK    [{++step}] {msg}");
        int Fail(string msg)
        {
            Console.Error.WriteLine($"  GAGAL {msg}");
            return 1;
        }

        // 1. Muat test vector resmi
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "sdk_vectors.json");
        if (!File.Exists(fixturePath))
            fixturePath = Path.Combine(Directory.GetCurrentDirectory(), "fixtures", "sdk_vectors.json");
        if (!File.Exists(fixturePath))
            return Fail($"fixtures/sdk_vectors.json tidak ditemukan di: {fixturePath}");

        var vectors = JsonSerializer.Deserialize<SdkVectors>(File.ReadAllText(fixturePath))!;
        Ok($"fixture sdk_vectors.json termuat ({fixturePath})");

        // 2. Uji load native DLL + GenerateKeypairs
        try
        {
            using var probe = new XChat();
            var generated = probe.GenerateKeypairs();
            if (string.IsNullOrEmpty(generated.PublicKey.IdentityPublicKeySignature))
                return Fail("GenerateKeypairs mengembalikan payload kosong.");
            Ok($"native chat_xdk_ffi.dll termuat; GenerateKeypairs OK (fingerprint {generated.PublicKey.PublicKeyFingerprint})");
        }
        catch (DllNotFoundException ex)
        {
            return Fail($"DLL native tidak termuat: {ex.Message}");
        }

        // 3. Round-trip ExportKeys(64B) -> Lock -> ImportKeys
        using (var chat = new XChat())
        {
            chat.GenerateKeypairs();
            var pubBefore = chat.GetPublicKeys();
            var exported = chat.ExportKeys()
                ?? throw new InvalidOperationException("ExportKeys mengembalikan null.");
            if (exported.Length != 64)
                return Fail($"ExportKeys mengharapkan 64 byte (identity+signing), dapat {exported.Length}.");

            chat.Lock();
            if (chat.IsUnlocked)
                return Fail("Lock() tidak membersihkan kunci di memori.");

            chat.ImportKeys(new ImportKeysParams(exported));
            if (!chat.IsUnlocked)
                return Fail("ImportKeys gagal membuka sesi.");

            var pubAfter = chat.GetPublicKeys();
            if (pubAfter.Identity != pubBefore.Identity || pubAfter.Signing != pubBefore.Signing)
                return Fail("Kunci publik berubah setelah Export/Import round-trip.");
            Ok($"round-trip ExportKeys({exported.Length}B) -> Lock -> ImportKeys OK, kunci identik");
        }

        // 4. Dekripsi offline event Message dari test vector
        try
        {
            using var chat = new XChat();
            chat.ImportKeys(new ImportKeysParams(Convert.FromBase64String(vectors.PrivateKeysConcatB64))
            {
                SigningKeyVersion = vectors.EventSigningKeyVersion,
            });
            chat.SetIdentity(new SetIdentityParams(vectors.EventSenderId, vectors.EventSigningKeyVersion));
            chat.SetCacheKeys(true);
            chat.SetSigningKeys(new[]
            {
                new SigningKeyEntry
                {
                    UserId = vectors.EventSenderId,
                    PublicKeyVersion = vectors.EventSigningKeyVersion,
                    PublicKey = vectors.SigningPublicB64,
                    IdentityPublicKey = vectors.IdentityPublicB64,
                    IdentityPublicKeySignature = vectors.IdentityPublicKeySignatureB64,
                },
            });

            var conversationKeys = new Dictionary<string, byte[]>
            {
                [vectors.EventConversationKeyVersion] = Convert.FromBase64String(vectors.ConversationKeyB64),
            };
            var evt = chat.DecryptEvent(new DecryptEventParams(vectors.EventMessageB64, conversationKeys));

            var convId = evt.GetProperty("conversation_id").GetString();
            if (convId != vectors.EventConversationId)
                return Fail($"conversation_id tidak cocok: '{convId}' != '{vectors.EventConversationId}'");

            string? text = null;
            if (evt.TryGetProperty("content", out var contentEl) &&
                contentEl.ValueKind == JsonValueKind.Object &&
                contentEl.TryGetProperty("text", out var tEl))
                text = tEl.GetString();
            text ??= evt.TryGetProperty("text", out var direct) ? direct.GetString() : null;

            if (text != vectors.EventMessageText)
                return Fail($"teks hasil dekripsi tidak cocok:\n  hasil : '{text}'\n  vector: '{vectors.EventMessageText}'");
            Ok($"dekripsi offline event Message OK: \"{text}\"");

            // 5. Dekripsi offline lampiran terenkripsi (DecryptFile) dari test vector
            var mediaEvt = chat.DecryptEvent(new DecryptEventParams(vectors.EventConversationKeyMediaB64, conversationKeys));
            var parsedMediaMsg = CrmChatSink.FromEncryptedEvent(mediaEvt, vectors.EventConversationKeyMediaB64, conversationKeys);
            if (parsedMediaMsg is null || parsedMediaMsg.Attachments.Count == 0)
                return Fail("ekstraksi metadata lampiran dari fixture media gagal.");

            var encMedia = Convert.FromBase64String(vectors.MediaConversationKeyEncryptedB64);
            var expectedMedia = Convert.FromBase64String(vectors.MediaConversationKeyPlaintextB64);
            var decMedia = chat.DecryptFile(new DecryptFileParams(
                encMedia, vectors.EventConversationKeyMediaB64, conversationKeys, vectors.MediaConversationKeyHashKey));
            if (!decMedia.SequenceEqual(expectedMedia))
                return Fail("hasil DecryptFile tidak cocok dengan plaintext fixture.");
            Ok($"dekripsi offline lampiran DecryptFile OK ({decMedia.Length}B, file '{parsedMediaMsg.Attachments[0].FileName}')");
        }
        catch (ChatXdkException ex)
        {
            return Fail($"dekripsi fixture gagal ({ex.Kind}): {ex.Message}");
        }

        Console.WriteLine("\n=== check-native LULUS ===");
        return 0;
    }

    // ---------------- check-keystore (alias: fase2) ----------------

    /// <summary>
    /// Uji offline KeyStore DPAPI pada direktori sementara terisolasi (%TEMP%)
    /// sehingga aman dijalankan kapan pun tanpa menimpa chat_keys.dat terdaftar.
    /// </summary>
    public static int CheckKeyStore()
    {
        Console.WriteLine("=== check-keystore: uji DPAPI & ConversationKeys (terisolasi, offline) ===\n");

        var tempDir = Path.Combine(Path.GetTempPath(), "BICARA-KeyStoreCheck-" + Guid.NewGuid().ToString("N"));
        KeyStore.StoreDirectoryOverride = tempDir;
        try
        {
            // 1. Generate & simpan
            var blob = KeyStore.GenerateAndStore();
            Console.WriteLine("  OK    [1] key baru digenerate & blob DPAPI disimpan (store sementara):");
            Console.WriteLine($"            {KeyStore.BlobPath}");
            Console.WriteLine($"            fingerprint : {blob.Meta.PublicKeyFingerprint}");
            Console.WriteLine($"            dibuat (UTC): {blob.Meta.CreatedUtc:O}");

            // 2. Pastikan kunci privat tidak bocor plaintext ke disk
            var rawKeys = KeyStore.Unprotect(Convert.FromBase64String(blob.ProtectedKeysB64));
            var fileBytes = File.ReadAllBytes(KeyStore.BlobPath);
            var leak = false;
            for (var i = 0; i + 16 <= rawKeys.Length; i++)
            {
                if (IndexOf(fileBytes, rawKeys, i, 16) >= 0) { leak = true; break; }
            }
            if (leak)
            {
                Console.Error.WriteLine("  GAGAL [2] plaintext kunci privat ditemukan di file blob!");
                return 1;
            }
            Console.WriteLine("  OK    [2] tidak ada byte kunci privat (>=16B) di file blob — DPAPI bekerja");

            // 3 & 4. Round-trip OpenChat (verifikasi fingerprint & VerifyKeyBinding format SPKI)
            using (var reopened = KeyStore.OpenChat(out var meta))
            {
                if (!reopened.IsUnlocked)
                {
                    Console.Error.WriteLine("  GAGAL [3] sesi tidak unlocked setelah OpenChat.");
                    return 1;
                }
                var fpAfter = reopened.GetPublicKeyFingerprint();
                if (fpAfter != blob.Meta.PublicKeyFingerprint)
                {
                    Console.Error.WriteLine($"  GAGAL [3] fingerprint berubah setelah round-trip ({fpAfter} != {blob.Meta.PublicKeyFingerprint}).");
                    return 1;
                }
                var bindingOk = !string.IsNullOrEmpty(meta.IdentityPublicKeySignature) &&
                                reopened.VerifyKeyBinding(meta.IdentityPublic, meta.SigningPublic, meta.IdentityPublicKeySignature);
                if (!bindingOk)
                {
                    Console.Error.WriteLine("  GAGAL [3] VerifyKeyBinding gagal setelah round-trip DPAPI.");
                    return 1;
                }
                Console.WriteLine("  OK    [3] OpenChat: unprotect DPAPI -> ImportKeys, fingerprint & VerifyKeyBinding identik");
                Console.WriteLine(meta.SigningKeyVersion is null
                    ? "  OK    [4] SigningKeyVersion masih null (belum registrasi)"
                    : $"  INFO  [4] SigningKeyVersion terisi: {meta.SigningKeyVersion}");
            }

            // 5. Round-trip ConversationKeys terproteksi DPAPI + cek anti-leak
            var dummyConvKey = RandomNumberGenerator.GetBytes(32);
            var dummyConvKeyB64 = Convert.ToBase64String(dummyConvKey);
            KeyStore.StoreConversationKey("200-100", "v-test-1", dummyConvKey);

            var blobText = File.ReadAllText(KeyStore.BlobPath);
            if (blobText.Contains(dummyConvKeyB64, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("  GAGAL [5] conversation key tersimpan plaintext (base64) di file blob!");
                return 1;
            }
            var loadedConvKeys = KeyStore.GetConversationKeys("100:200");
            if (!loadedConvKeys.TryGetValue("v-test-1", out var roundTripped) || !roundTripped.SequenceEqual(dummyConvKey))
            {
                Console.Error.WriteLine("  GAGAL [5] conversation key tidak cocok setelah round-trip DPAPI.");
                return 1;
            }
            Console.WriteLine("  OK    [5] ConversationKeys terproteksi DPAPI & round-trip kanonik (200-100 == 100:200) utuh");

            Console.WriteLine("\n=== check-keystore LULUS ===");
            return 0;
        }
        finally
        {
            KeyStore.StoreDirectoryOverride = null;
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Abaikan bila file sementara masih terkunci antivirus.
            }
        }
    }

    // ---------------- verify-keys ----------------

    /// <summary>
    /// Uji konsistensi kriptografi chat_keys.dat lokal tanpa jaringan
    /// (wajib dijalankan sebelum register-keys agar tidak membakar kuota 5 req/24 jam).
    /// </summary>
    public static int VerifyKeys()
    {
        Console.WriteLine("=== verify-keys: uji konsistensi key blob lokal (tanpa jaringan) ===\n");
        var failures = 0;

        void Check(bool ok, string label)
        {
            Console.WriteLine($"  [{(ok ? "OK   " : "GAGAL")}] {label}");
            if (!ok) failures++;
        }

        // Kontrol positif: keypair sementara di memori
        Console.WriteLine("Kontrol positif (keypair sementara di memori, tidak disimpan):");
        try
        {
            using var fresh = new XChat();
            var freshPayload = fresh.GenerateKeypairs();
            Check(
                fresh.VerifyKeyBinding(
                    freshPayload.PublicKey.PublicKey,
                    freshPayload.PublicKey.SigningPublicKey,
                    freshPayload.PublicKey.IdentityPublicKeySignature),
                "VerifyKeyBinding keypair baru");
            Check(
                !string.IsNullOrEmpty(freshPayload.PublicKey.RegistrationMethod),
                $"RegistrationMethod keypair baru = '{freshPayload.PublicKey.RegistrationMethod}'");

            var freshExport = fresh.ExportKeys()
                ?? throw new InvalidOperationException("ExportKeys null");
            using var freshReimported = new XChat();
            freshReimported.ImportKeys(new ImportKeysParams(freshExport));
            Check(
                freshReimported.VerifyKeyBinding(
                    freshPayload.PublicKey.PublicKey,
                    freshPayload.PublicKey.SigningPublicKey,
                    freshPayload.PublicKey.IdentityPublicKeySignature),
                "kontrol round-trip: VerifyKeyBinding lulus setelah Export->Import");
        }
        catch (Exception ex)
        {
            Check(false, $"kontrol positif melempar exception: {ex.Message}");
        }
        Console.WriteLine();

        // Pemeriksaan blob tersimpan
        Console.WriteLine($"Blob tersimpan: {KeyStore.BlobPath}");
        var blob = KeyStore.Load();
        if (blob is null)
        {
            Console.Error.WriteLine("Belum ada key blob — jalankan 'unlock-pin' atau 'keygen' dulu.");
            return 1;
        }
        var meta = blob.Meta;
        Console.WriteLine($"  dibuat (UTC): {meta.CreatedUtc:u}");
        Console.WriteLine($"  metode      : {meta.RegistrationMethod}");
        Console.WriteLine($"  fingerprint : {meta.PublicKeyFingerprint}");
        Console.WriteLine($"  registrasi  : {(meta.SigningKeyVersion is null ? "belum terdaftar" : $"v{meta.SigningKeyVersion} pada {meta.RegisteredUtc:u}")}\n");

        byte[] exported;
        try
        {
            exported = KeyStore.Unprotect(Convert.FromBase64String(blob.ProtectedKeysB64));
            Check(true, "blob terbuka oleh DPAPI (user + mesin yang sama)");
        }
        catch (Exception ex)
        {
            Check(false, $"blob tidak bisa dibuka DPAPI: {ex.Message} — jalankan 'unlock-pin' atau 'keygen' ulang.");
            Console.WriteLine($"\n=== verify-keys SELESAI: {failures} kegagalan ===");
            return 1;
        }

        using var chat = new XChat();
        chat.ImportKeys(new ImportKeysParams(exported)
        {
            SigningKeyVersion = meta.SigningKeyVersion,
        });

        // 1. Fingerprint blob == meta
        string? fp = null;
        try { fp = chat.GetPublicKeyFingerprint(); } catch { /* dilaporkan via Check */ }
        Check(fp is not null && fp == meta.PublicKeyFingerprint,
            $"fingerprint (blob) == meta ({fp ?? "<exception>"})");

        // 2. VerifyKeyBinding (format SPKI)
        if (!string.IsNullOrEmpty(meta.IdentityPublicKeySignature))
        {
            var bindingOk = false;
            string? bindingErr = null;
            try
            {
                bindingOk = chat.VerifyKeyBinding(meta.IdentityPublic, meta.SigningPublic, meta.IdentityPublicKeySignature);
            }
            catch (Exception ex)
            {
                bindingErr = ex.Message;
            }
            Check(bindingOk,
                bindingErr is null
                    ? "VerifyKeyBinding(identity, signing, identity_signature)"
                    : $"VerifyKeyBinding melempar exception: {bindingErr}");
        }
        else
        {
            Check(false, "meta.identity_public_key_signature kosong — jalankan 'unlock-pin' atau 'keygen' ulang.");
        }

        // 3. Round-trip Sign/Verify
        try
        {
            var data = Encoding.UTF8.GetBytes("bicara-verify-keys-" + DateTime.UtcNow.Ticks);
            var sig = chat.Sign(data);
            Check(chat.Verify(meta.SigningPublic, sig, data),
                "round-trip Sign/Verify dengan signing key blob");
        }
        catch (Exception ex) when (ex.Message.Contains("signing_key_version", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  [SKIP ] round-trip Sign/Verify: {ex.Message}");
        }
        catch (Exception ex)
        {
            Check(false, $"round-trip Sign/Verify melempar exception: {ex.Message}");
        }

        // 4. signing_public_key_signature (opsional untuk kunci hasil unlock-pin)
        if (!string.IsNullOrEmpty(meta.SigningPublicKeySignature))
        {
            var sigSigValid = false;
            try { Convert.FromBase64String(meta.SigningPublicKeySignature); sigSigValid = true; } catch { }
            Check(sigSigValid, "meta.signing_public_key_signature valid base64");
        }
        else if (meta.RegistrationMethod == "JuiceboxPin")
        {
            Console.WriteLine("  [SKIP ] signing_public_key_signature (tidak dibutuhkan untuk kunci JuiceboxPin yang sudah terdaftar)");
        }
        else
        {
            Check(false, "meta.signing_public_key_signature kosong (wajib untuk register-keys)");
        }

        // 5. Preview payload registrasi bila belum terdaftar
        if (meta.SigningKeyVersion is null)
        {
            Console.WriteLine("\nPreview body JSON untuk register-keys (bukan request nyata):");
            var pk = new JsonObject
            {
                ["public_key"] = meta.IdentityPublic,
                ["signing_public_key"] = meta.SigningPublic,
                ["identity_public_key_signature"] = meta.IdentityPublicKeySignature,
                ["registration_method"] = meta.RegistrationMethod,
            };
            if (meta.SigningPublicKeySignature is not null)
                pk["signing_public_key_signature"] = meta.SigningPublicKeySignature;
            var body = new JsonObject { ["public_key"] = pk };
            Console.WriteLine(body.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        Console.WriteLine($"\n=== verify-keys SELESAI: {failures} kegagalan ===");
        return failures > 0 ? 1 : 0;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int needleStart, int length)
    {
        for (var i = 0; i + length <= haystack.Length; i++)
        {
            var match = true;
            for (var j = 0; j < length; j++)
                if (haystack[i + j] != needle[needleStart + j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
