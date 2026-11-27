// ============================================================
// ChatSyncCommands — penarikan & sinkronisasi DM X -> Dynamics CRM
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Perintah:
//   pull (alias: pull-chat) : tarik DM terenkripsi (X Chat) + plaintext (/2/dm_events)
//                             dan cetak ke console tanpa menulis ke CRM.
//   sync (alias: sync-chat) : tarik DM terenkripsi + plaintext dalam 1 flow lalu
//                             simpan ke CRM bi_twitter (dedupe via bi_twittid).
//
// Opsi flag:
//   --dry-run        : (khusus sync) tampilkan preview tanpa menulis ke CRM
//   --full           : (khusus sync) abaikan watermark 'X Last Sync' (tarik ulang penuh)
//   --verbose        : tampilkan log diagnostik dekripsi & kunci lebih rinci
//   --encrypted-only : hanya tarik DM terenkripsi (X Chat E2EE)
//   --plaintext-only : hanya tarik DM tidak terenkripsi (/2/dm_events)
// ============================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using ChatXdk;
using XChat = ChatXdk.Chat;

namespace SocialMediaIntegration.Chat;

internal static class ChatSyncCommands
{
    private const string DefaultSyncConfigName = "X Last Sync";
    private static readonly TimeSpan WatermarkSafetyBuffer = TimeSpan.FromMinutes(2);

    private readonly record struct PullOptions(
        bool DryRun,
        bool FullSync,
        bool Verbose,
        bool IncludeEncrypted,
        bool IncludePlaintext);

    private static bool TryParseOptions(string[] args, out PullOptions options)
    {
        var dryRun = args.Contains("--dry-run");
        var fullSync = args.Contains("--full");
        var verbose = args.Contains("--verbose");
        var encryptedOnly = args.Contains("--encrypted-only");
        var plaintextOnly = args.Contains("--plaintext-only");
        if (encryptedOnly && plaintextOnly)
        {
            Console.Error.WriteLine("Flag --encrypted-only dan --plaintext-only tidak dapat digabung.");
            options = default;
            return false;
        }
        options = new PullOptions(dryRun, fullSync, verbose, !plaintextOnly, !encryptedOnly);
        return true;
    }

    // ---------------- pull (alias: pull-chat) ----------------

    public static async Task<int> PullAsync(string[] args)
    {
        if (!TryParseOptions(args, out var opt)) return 2;

        Console.WriteLine("=== pull: tarik DM X (terenkripsi + plaintext, read-only) ===\n");

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var myUserId = await client.GetAuthenticatedUserIdAsync();
        Console.WriteLine($"Login sebagai user id {myUserId}\n");

        var messages = await PullAllMessagesUnifiedAsync(
            client, myUserId, opt.IncludeEncrypted, opt.IncludePlaintext, opt.Verbose,
            stopBeforeUtc: null, downloadAttachments: false);
        foreach (var (convId, msg) in messages)
        {
            var tag = msg.IsEncrypted ? "E2EE " : "PLAIN";
            var attSuffix = msg.Attachments.Count > 0 ? $" [+{msg.Attachments.Count} lampiran]" : "";
            Console.WriteLine($"  [{tag}] [{convId}] {msg.SenderId}: {msg.Text}{attSuffix}");
        }

        var e2eeCount = messages.Count(m => m.Message.IsEncrypted);
        var plainCount = messages.Count - e2eeCount;
        Console.WriteLine($"\n=== pull SELESAI: {messages.Count} pesan ({e2eeCount} E2EE, {plainCount} plaintext) ===");
        return 0;
    }

    // ---------------- sync (alias: sync-chat) ----------------

    public static async Task<int> SyncAsync(string[] args)
    {
        if (!TryParseOptions(args, out var opt)) return 2;

        Console.WriteLine(opt.DryRun
            ? "=== sync --dry-run: simulasi tarik DM -> CRM (tidak menulis) ===\n"
            : "=== sync: tarik DM (terenkripsi + plaintext) -> CRM bi_twitter ===\n");

        var connStr = Environment.GetEnvironmentVariable("CRM_CONNECTION_STRING");
        CrmWebApi? crm = null;
        if (!opt.DryRun && !string.IsNullOrWhiteSpace(connStr))
        {
            Console.WriteLine("Menghubungkan ke CRM (Web API + NTLM)...");
            try
            {
                crm = new CrmWebApi(connStr);
                Console.WriteLine($"  endpoint: {crm.ApiBase}");
                var who = await crm.PingAsync();
                Console.WriteLine($"CRM OK (WhoAmI UserId {who})\n");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Koneksi CRM gagal: {ex.Message}");
                return 1;
            }
        }
        else
        {
            var reason = opt.DryRun ? "flag --dry-run aktif" : "CRM_CONNECTION_STRING tidak diisi";
            Console.WriteLine($"Mode DRY-RUN ({reason}) — tidak ada yang ditulis ke CRM.\n");
        }

        using var crmScope = crm;

        // Baca watermark 'X Last Sync' dari new_configuration (kecuali --full).
        var configName = Environment.GetEnvironmentVariable("CRM_SYNC_CONFIG_NAME");
        if (string.IsNullOrWhiteSpace(configName)) configName = DefaultSyncConfigName;

        string? configId = null;
        DateTime? lastSyncUtc = null;
        DateTime? effectiveCutoffUtc = null;
        var syncStartedUtc = DateTime.UtcNow;

        if (crm is not null && !opt.FullSync)
        {
            try
            {
                var (id, rawVal) = await crm.GetConfigurationAsync(configName);
                configId = id;
                if (CrmUtility.TryParseXTimestamp(rawVal, out var parsedLastSync))
                {
                    lastSyncUtc = parsedLastSync;
                    effectiveCutoffUtc = parsedLastSync - WatermarkSafetyBuffer;
                    Console.WriteLine(
                        $"Watermark '{configName}': {parsedLastSync:yyyy-MM-ddTHH:mm:ssZ} " +
                        $"(cutoff dgn buffer {WatermarkSafetyBuffer.TotalMinutes:0}m: {effectiveCutoffUtc:yyyy-MM-ddTHH:mm:ssZ})\n");
                }
                else if (id is not null)
                {
                    Console.WriteLine(
                        $"Watermark '{configName}' ditemukan tetapi kosong/belum valid ('{rawVal ?? ""}') — sinkronisasi awal.\n");
                }
                else
                {
                    Console.WriteLine(
                        $"Watermark '{configName}' belum ada di new_configuration — sinkronisasi awal (record akan dibuat otomatis).\n");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [peringatan] gagal membaca watermark '{configName}': {ex.Message} — lanjut tanpa cutoff.\n");
            }
        }
        else if (opt.FullSync)
        {
            Console.WriteLine($"Mode --full aktif — watermark '{configName}' diabaikan untuk penarikan ini.\n");
        }

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var myUserId = await client.GetAuthenticatedUserIdAsync();
        Console.WriteLine($"Login sebagai user id {myUserId}\n");

        var skipAttEnv = Environment.GetEnvironmentVariable("CRM_SKIP_ATTACHMENTS");
        var downloadAttachments = crm is not null && !string.Equals(skipAttEnv, "true", StringComparison.OrdinalIgnoreCase);

        var allMessages = await PullAllMessagesUnifiedAsync(
            client, myUserId, opt.IncludeEncrypted, opt.IncludePlaintext, opt.Verbose,
            effectiveCutoffUtc, downloadAttachments);

        var skippedByWatermark = 0;
        IReadOnlyList<(string ConversationId, ChatMessage Message)> messages;
        if (effectiveCutoffUtc is { } cut)
        {
            var filtered = new List<(string, ChatMessage)>(allMessages.Count);
            foreach (var item in allMessages)
            {
                if (item.Message.CreatedUtc < cut)
                    skippedByWatermark++;
                else
                    filtered.Add(item);
            }
            messages = filtered;
            if (skippedByWatermark > 0)
                Console.WriteLine($"Watermark '{configName}': {skippedByWatermark} pesan historis dilewati sebelum query CRM.\n");
        }
        else
        {
            messages = allMessages;
        }

        var created = 0;
        var skipped = 0;
        var failed = 0;
        DateTime? maxProcessedUtc = null;

        if (messages.Count > 0)
        {
            // Batch lookup profil lawan bicara (maks 100 ID/request di XApiChatClient).
            var counterpartIds = messages
                .SelectMany(m => m.ConversationId.Split('-', ':'))
                .Concat(messages.Select(m => m.Message.SenderId))
                .Where(id => !string.IsNullOrWhiteSpace(id) && id != myUserId)
                .Distinct()
                .ToList();
            var profiles = await client.GetUsersByIdsAsync(counterpartIds);

            foreach (var (_, msg) in messages)
            {
                var counterId = msg.SenderId == myUserId
                    ? msg.ConversationId.Split('-', ':').FirstOrDefault(p => p != myUserId) ?? msg.SenderId
                    : msg.SenderId;
                profiles.TryGetValue(counterId, out var prof);
                var name = prof.Name ?? counterId;
                var screen = prof.Username ?? counterId;

                if (crm is null)
                {
                    var dir = msg.SenderId != myUserId ? "IN " : "OUT";
                    var tag = msg.IsEncrypted ? "E2EE " : "PLAIN";
                    var attSuffix = msg.Attachments.Count > 0 ? $" [+{msg.Attachments.Count} lampiran]" : "";
                    Console.WriteLine($"  [dry-run {tag} {dir}] {screen}: {Truncate(msg.Text, 80)}{attSuffix}");
                    continue;
                }

                try
                {
                    if (await CrmChatSink.SaveMessageAsync(crm, msg, myUserId, name, screen))
                        created++;
                    else
                        skipped++;

                    if (maxProcessedUtc is null || msg.CreatedUtc > maxProcessedUtc.Value)
                        maxProcessedUtc = msg.CreatedUtc;
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.Error.WriteLine($"  Gagal simpan {msg.MessageId}: {ex.Message}");
                }
            }
        }
        else
        {
            Console.WriteLine("Tidak ada pesan baru sejak watermark terakhir.");
        }

        // Perbarui watermark 'X Last Sync' di new_configuration bila run sukses penuh
        // (tidak dry-run, tidak ada error simpan, dan bukan mode filter 1 sumber saja).
        if (crm is not null)
        {
            if (failed > 0)
            {
                Console.Error.WriteLine(
                    $"  [watermark] '{configName}' tidak dimajukan karena ada {failed} pesan gagal disimpan (akan dicoba ulang di run berikutnya).");
            }
            else if (!opt.IncludeEncrypted || !opt.IncludePlaintext)
            {
                Console.WriteLine(
                    $"  [watermark] '{configName}' tidak diperbarui karena mode filter sumber (--encrypted-only / --plaintext-only) aktif.");
            }
            else
            {
                var newWatermark = maxProcessedUtc is { } maxMsg && maxMsg > syncStartedUtc
                    ? maxMsg
                    : syncStartedUtc;
                if (lastSyncUtc is { } prev && newWatermark < prev)
                    newWatermark = prev;

                var wmText = newWatermark.ToString("yyyy-MM-ddTHH:mm:ssZ");
                try
                {
                    await crm.UpsertConfigurationAsync(configName, wmText, configId);
                    Console.WriteLine($"  [watermark] '{configName}' diperbarui -> {wmText}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [peringatan] gagal memperbarui watermark '{configName}': {ex.Message}");
                }
            }
        }

        var e2eeCount = messages.Count(m => m.Message.IsEncrypted);
        var plainCount = messages.Count - e2eeCount;
        Console.WriteLine(
            $"\n=== sync SELESAI: {created} record baru, {skipped} duplikat dilewati" +
            (skippedByWatermark > 0 ? $", {skippedByWatermark} dilewati watermark" : "") +
            (failed > 0 ? $", {failed} gagal" : "") +
            $" (diproses: {e2eeCount} E2EE, {plainCount} plaintext) ===");
        return failed > 0 ? 1 : 0;
    }

    // ---------------- Inti penarikan gabungan (E2EE + Plaintext) ----------------

    private static async Task<IReadOnlyList<(string ConversationId, ChatMessage Message)>> PullAllMessagesUnifiedAsync(
        XApiChatClient client, string myUserId, bool includeEncrypted, bool includePlaintext,
        bool verbose, DateTime? stopBeforeUtc, bool downloadAttachments)
    {
        var combined = new List<(string ConversationId, ChatMessage Message)>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        void AddRangeDedup(IReadOnlyList<(string ConversationId, ChatMessage Message)> items)
        {
            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Message.MessageId) || seenIds.Add(item.Message.MessageId))
                    combined.Add(item);
            }
        }

        if (includeEncrypted)
        {
            Console.WriteLine("--- Sumber 1: DM Terenkripsi (X Chat / E2EE) ---");
            try
            {
                using var chat = KeyStore.OpenChat(out var meta);
                if (meta.SigningKeyVersion is null)
                {
                    Console.Error.WriteLine(
                        "  [lewati E2EE] SigningKeyVersion kosong — jalankan 'unlock-pin' atau 'register-keys' dulu.");
                }
                else
                {
                    chat.SetIdentity(new SetIdentityParams(myUserId, meta.SigningKeyVersion));
                    chat.SetCacheKeys(true);
                    Console.WriteLine($"  Menggunakan key v{meta.SigningKeyVersion}");
                    var encrypted = await PullEncryptedMessagesAsync(
                        client, chat, myUserId, verbose, stopBeforeUtc, downloadAttachments);
                    AddRangeDedup(encrypted);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [gagal sumber E2EE] {ex.Message}");
                if (!includePlaintext) throw;
            }
            Console.WriteLine();
        }

        if (includePlaintext)
        {
            Console.WriteLine("--- Sumber 2: DM Tidak Terenkripsi (Plaintext /2/dm_events) ---");
            try
            {
                var plaintext = await PullPlaintextMessagesAsync(
                    client, myUserId, verbose, stopBeforeUtc, downloadAttachments);
                AddRangeDedup(plaintext);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [gagal sumber plaintext] {ex.Message}");
                if (!includeEncrypted) throw;
            }
            Console.WriteLine();
        }

        return combined;
    }

    private static async Task<IReadOnlyList<(string ConversationId, ChatMessage Message)>> PullEncryptedMessagesAsync(
        XApiChatClient client, XChat chat, string myUserId, bool verbose,
        DateTime? stopBeforeUtc, bool downloadAttachments)
    {
        var all = new List<(string, ChatMessage)>();
        var conversations = await client.GetAllConversationsAsync();
        Console.WriteLine($"  {conversations.Count} percakapan X Chat ditemukan.");

        // Kumpulkan semua partisipan (termasuk akun sendiri untuk versi key aplikasi resmi).
        var participantIds = conversations
            .SelectMany(c => (c["id"]?.GetValue<string>() ?? "").Split('-'))
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();

        var entries = new List<SigningKeyEntry>();
        var ownMeta = KeyStore.Load()?.Meta;
        if (ownMeta is not null && ownMeta.SigningKeyVersion is not null)
        {
            entries.Add(new SigningKeyEntry
            {
                UserId = myUserId,
                PublicKeyVersion = ownMeta.SigningKeyVersion,
                PublicKey = ownMeta.SigningPublic,
                IdentityPublicKey = ownMeta.IdentityPublic,
                IdentityPublicKeySignature = ownMeta.IdentityPublicKeySignature ?? "",
            });
        }

        foreach (var uid in participantIds)
        {
            try
            {
                var records = await client.GetPublicKeysAsync(uid);
                var added = 0;
                foreach (var r in records.OfType<JsonObject>())
                {
                    var version = r["public_key_version"]?.GetValue<string>();
                    var identity = r["public_key"]?.GetValue<string>();
                    var signing = r["signing_public_key"]?.GetValue<string>();
                    var sig = r["identity_public_key_signature"]?.GetValue<string>();
                    if (version is null || identity is null || signing is null || sig is null)
                        continue;
                    if (uid == myUserId && ownMeta?.SigningKeyVersion == version)
                        continue;
                    entries.Add(new SigningKeyEntry
                    {
                        UserId = uid,
                        PublicKeyVersion = version,
                        PublicKey = signing,
                        IdentityPublicKey = identity,
                        IdentityPublicKeySignature = sig,
                    });
                    added++;
                }
                if (verbose) Console.WriteLine($"  key user {uid}: {added} versi terdaftar.");
                if (added == 0 && uid != myUserId)
                    Console.WriteLine($"  [peringatan] user {uid} belum punya public key — DM E2EE darinya tidak dapat diverifikasi.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [peringatan] gagal ambil key user {uid}: {ex.Message}");
            }
        }

        foreach (var e in entries)
        {
            try
            {
                if (!chat.VerifyKeyBinding(e.IdentityPublicKey, e.PublicKey, e.IdentityPublicKeySignature))
                    Console.WriteLine($"  [diag] binding {e.UserId} v{e.PublicKeyVersion}: GAGAL VerifyKeyBinding");
            }
            catch (Exception ex)
            {
                if (verbose) Console.WriteLine($"  [diag] binding {e.UserId} v{e.PublicKeyVersion}: {ex.Message}");
            }
        }

        if (entries.Count > 0)
            chat.SetSigningKeys(entries);

        foreach (var conv in conversations)
        {
            var convId = conv["id"]?.GetValue<string>() ?? "?";
            try
            {
                var (events, keyEvents) = await client.GetAllEventsAsync(convId);
                if (verbose)
                    Console.WriteLine($"  [{convId}] batch: {events.Count} event pesan + {keyEvents.Count} key change");

                var batch = new List<string>(events);
                batch.AddRange(keyEvents);

                var result = chat.DecryptEvents(new DecryptEventsParams(batch));
                var convMessages = new List<ChatMessage>();
                var nonMessage = 0;

                foreach (var dm in result.Events)
                {
                    var msg = CrmChatSink.FromEncryptedEvent(dm.Event, dm.OriginalB64);
                    if (msg is not null)
                    {
                        all.Add((convId, msg));
                        convMessages.Add(msg);
                    }
                    else
                    {
                        nonMessage++;
                        if (verbose)
                        {
                            var type = dm.Event.TryGetProperty("type", out var t) ? t.GetString() : "?";
                            Console.WriteLine($"  [{convId}] event non-teks: type={type}");
                        }
                    }
                }

                // Fallback recovery memakai conversation key lokal (KeyStore DPAPI).
                var storedKeys = KeyStore.GetConversationKeys(convId);
                var recovered = 0;
                if (storedKeys.Count > 0)
                {
                    foreach (var (idx, errText) in result.Errors)
                    {
                        if (!errText.Contains("no matching key found")) continue;
                        if (!int.TryParse(idx, out var i) || i < 0 || i >= batch.Count) continue;
                        try
                        {
                            var ev = chat.DecryptEvent(new DecryptEventParams(batch[i], storedKeys));
                            var msg = CrmChatSink.FromEncryptedEvent(ev, batch[i], storedKeys);
                            if (msg is not null)
                            {
                                all.Add((convId, msg));
                                convMessages.Add(msg);
                                recovered++;
                                if (verbose)
                                    Console.WriteLine($"  [{convId}] dipulihkan via KeyStore (event #{idx}): {Truncate(msg.Text, 60)}");
                            }
                        }
                        catch
                        {
                            // Tetap tercatat di result.Errors.
                        }
                    }
                }

                // Unduh & dekripsi lampiran media (hanya bila sync ke CRM aktif dan pesan lolos watermark).
                if (downloadAttachments)
                {
                    var maxBytes = GetMaxAttachmentBytes();
                    foreach (var msg in convMessages)
                    {
                        if (msg.Attachments.Count == 0) continue;
                        if (stopBeforeUtc is { } cut && msg.CreatedUtc < cut) continue;
                        await PopulateAttachmentsAsync(client, chat, convId, msg, maxBytes, verbose);
                    }
                }

                if (verbose)
                {
                    foreach (var err in result.Errors)
                        Console.WriteLine($"  [{convId}] error dekripsi: {err}");
                    DumpDiag(convId, "missing_signing_keys", result.MissingSigningKeys);
                    DumpDiag(convId, "unverified_key_changes", result.UnverifiedKeyChanges);
                    DumpDiag(convId, "rejected_signing_keys", result.RejectedSigningKeys);
                    DumpDiag(convId, "undecryptable_key_changes", result.UndecryptableKeyChanges);
                }

                var inc = convMessages.Count(m => m.SenderId != myUserId);
                var outg = convMessages.Count - inc;
                var summary = $"  [{convId}] {convMessages.Count} pesan ({inc} masuk / {outg} keluar)" +
                              (nonMessage > 0 ? $", {nonMessage} non-pesan" : "") +
                              (recovered > 0 ? $", {recovered} dipulihkan via KeyStore" : "") +
                              (result.Errors.Count > 0 ? $", {result.Errors.Count} gagal dekripsi (detail: --verbose)" : "");
                Console.WriteLine(summary);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{convId}] GAGAL: {ex.Message}");
            }
        }
        return all;
    }

    private static async Task<IReadOnlyList<(string ConversationId, ChatMessage Message)>> PullPlaintextMessagesAsync(
        XApiChatClient client, string myUserId, bool verbose, DateTime? stopBeforeUtc, bool downloadAttachments)
    {
        var maxPages = 5;
        var maxPagesEnv = Environment.GetEnvironmentVariable("X_DM_EVENTS_MAX_PAGES");
        if (!string.IsNullOrWhiteSpace(maxPagesEnv) && int.TryParse(maxPagesEnv, out var parsedPages))
            maxPages = parsedPages;

        var (rawEvents, mediaByKey) = await client.GetPlaintextDmEventsAsync(maxPages, stopBeforeUtc);
        var result = new List<(string, ChatMessage)>();
        var skippedNonText = 0;
        var maxBytes = GetMaxAttachmentBytes();

        foreach (var evt in rawEvents)
        {
            var msg = CrmChatSink.FromPlaintextDmEvent(evt, mediaByKey);
            if (msg is not null)
            {
                if (downloadAttachments && msg.Attachments.Count > 0 &&
                    (stopBeforeUtc is null || msg.CreatedUtc >= stopBeforeUtc.Value))
                {
                    await PopulateAttachmentsAsync(client, chat: null, msg.ConversationId, msg, maxBytes, verbose);
                }
                result.Add((msg.ConversationId, msg));
            }
            else
            {
                skippedNonText++;
            }
        }

        var inc = result.Count(x => x.Item2.SenderId != myUserId);
        var outg = result.Count - inc;
        var attCount = result.Sum(x => x.Item2.Attachments.Count);
        Console.WriteLine(
            $"  [plaintext /2/dm_events] {result.Count} pesan ({inc} masuk / {outg} keluar)" +
            (attCount > 0 ? $", {attCount} lampiran" : "") +
            (skippedNonText > 0 ? $", {skippedNonText} non-teks dilewati" : "") +
            (verbose ? $" (maks halaman: {(maxPages <= 0 ? "semua" : maxPages)})" : ""));
        return result;
    }

    private static long GetMaxAttachmentBytes()
    {
        var raw = Environment.GetEnvironmentVariable("CRM_MAX_ATTACHMENT_MB");
        var mb = int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 15;
        return mb * 1024L * 1024L;
    }

    private static async Task PopulateAttachmentsAsync(
        XApiChatClient client, XChat? chat, string convId, ChatMessage msg, long maxBytes, bool verbose)
    {
        foreach (var att in msg.Attachments)
        {
            if (att.FileSizeBytes is { } declared && declared > maxBytes)
            {
                Console.Error.WriteLine(
                    $"  [lewati lampiran] {att.FileName ?? att.MediaHashKey}: ukuran {declared / (1024 * 1024)} MB melebihi batas {maxBytes / (1024 * 1024)} MB.");
                continue;
            }

            try
            {
                byte[] decrypted;
                if (!string.IsNullOrEmpty(att.MediaHashKey) && chat is not null && !string.IsNullOrEmpty(msg.RawEventB64))
                {
                    var encBytes = await client.DownloadChatMediaAsync(convId, att.MediaHashKey);
                    if (encBytes.Length > maxBytes)
                    {
                        Console.Error.WriteLine($"  [lewati lampiran] {att.FileName ?? att.MediaHashKey}: ukuran terunduh melebihi batas.");
                        continue;
                    }
                    var decryptParams = msg.RecoveredConversationKeys is { Count: > 0 }
                        ? new DecryptFileParams(encBytes, msg.RawEventB64, msg.RecoveredConversationKeys, att.MediaHashKey)
                        : new DecryptFileParams(encBytes, msg.RawEventB64, att.MediaHashKey);
                    decrypted = chat.DecryptFile(decryptParams);
                }
                else if (!string.IsNullOrEmpty(att.LegacyUrl))
                {
                    decrypted = await client.DownloadUrlBytesAsync(att.LegacyUrl);
                    if (decrypted.Length > maxBytes)
                    {
                        Console.Error.WriteLine($"  [lewati lampiran] {att.FileName ?? att.LegacyUrl}: ukuran terunduh melebihi batas.");
                        continue;
                    }
                }
                else
                {
                    continue;
                }

                var detectedMime = ChatXdkUtilities.DetectMimeType(decrypted.AsSpan());
                att.MimeType = !string.IsNullOrWhiteSpace(detectedMime)
                    ? detectedMime
                    : CrmChatSink.GuessMimeTypeFromFileName(att.FileName, att.MediaType);
                if (string.IsNullOrWhiteSpace(att.FileName))
                    att.FileName = (att.MediaHashKey ?? "media") + CrmChatSink.GuessExtensionFromMimeOrType(att.MimeType, att.MediaType);
                att.DecryptedBytes = decrypted;

                if (verbose)
                    Console.WriteLine($"  [{convId}] lampiran siap: {att.FileName} ({decrypted.Length}B, {att.MimeType})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"  [peringatan] gagal unduh/dekripsi lampiran '{att.FileName ?? att.MediaHashKey}' ({msg.MessageId}): {ex.Message}");
            }
        }
    }

    private static void DumpDiag<TKey, TValue>(
        string convId, string label, IReadOnlyDictionary<TKey, TValue>? map) where TKey : notnull
    {
        if (map is null || map.Count == 0) return;
        Console.WriteLine($"  [{convId}] {label}:");
        foreach (var kv in map)
            Console.WriteLine($"    #{kv.Key}: {JsonSerializer.Serialize(kv.Value)}");
    }

    private static string Truncate(string s, int max) => s.Length >= max ? s.Substring(0, max) : s;
}
