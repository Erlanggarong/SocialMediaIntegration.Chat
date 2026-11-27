// ============================================================
// ChatReplyCommands — pengiriman DM langsung & antrian balasan CRM -> X
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Perintah:
//   send <userId> [pesan] [--file <path>] (alias: send-chat) : kirim 1 DM (+ lampiran opsional)
//   send-replies [--dry-run] [--verbose]                     : proses antrian balasan DM dari CRM
//                                                              (termasuk lampiran di Notes bi_twitter)
//
// Strategi pengiriman (otomatis hybrid):
//   - Jika penerima memiliki public key X Chat -> kirim terenkripsi E2EE
//     (POST /2/chat/conversations/{id}/keys, upload media bila ada, lalu /messages).
//   - Jika penerima belum mengaktifkan X Chat -> fallback ke DM plaintext
//     (POST /2/media/upload bila ada, lalu POST /2/dm_conversations/with/{userId}/messages).
// ============================================================

using System.Text.Json.Nodes;
using ChatXdk;
using XChat = ChatXdk.Chat;

namespace SocialMediaIntegration.Chat;

internal sealed record OutgoingAttachment(string FileName, string MimeType, byte[] Bytes);

internal static class ChatReplyCommands
{
    // ---------------- send (alias: send-chat) ----------------

    /// <summary>
    /// Kirim satu DM langsung ke userId tertentu (teks dan/atau lampiran --file).
    /// Format argumen: send &lt;userId&gt; [pesan...] [--file &lt;path&gt;]...
    /// </summary>
    public static async Task<int> SendAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Argumen kurang. Pakai: send <userId> [pesan] [--file <path>]");
            return 2;
        }

        var recipientId = args[1];
        var textParts = new List<string>();
        var filePaths = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--file")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Flag --file membutuhkan path file.");
                    return 2;
                }
                filePaths.Add(args[++i]);
            }
            else
            {
                textParts.Add(args[i]);
            }
        }

        var text = string.Join(' ', textParts);
        if (string.IsNullOrWhiteSpace(text) && filePaths.Count == 0)
        {
            Console.Error.WriteLine("Isi pesan atau minimal satu --file wajib diberikan.");
            return 2;
        }

        var attachments = new List<OutgoingAttachment>();
        foreach (var path in filePaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"File tidak ditemukan: {path}");
                return 1;
            }
            var bytes = await File.ReadAllBytesAsync(path);
            var fileName = Path.GetFileName(path);
            var mime = ChatXdkUtilities.DetectMimeType(bytes.AsSpan());
            if (string.IsNullOrWhiteSpace(mime))
                mime = CrmChatSink.GuessMimeTypeFromFileName(fileName);
            attachments.Add(new OutgoingAttachment(fileName, mime, bytes));
        }

        Console.WriteLine("=== send: kirim DM ke X ===\n");

        using var chat = KeyStore.OpenChat(out var meta);
        if (meta.SigningKeyVersion is null)
        {
            Console.Error.WriteLine("SigningKeyVersion kosong — jalankan 'unlock-pin' atau 'register-keys' dulu.");
            return 1;
        }

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var myUserId = await client.GetAuthenticatedUserIdAsync();
        chat.SetIdentity(new SetIdentityParams(myUserId, meta.SigningKeyVersion));
        chat.SetCacheKeys(true);
        Console.WriteLine($"Login sebagai user id {myUserId} (key v{meta.SigningKeyVersion})");

        var messageId = await SendDmAsync(client, chat, meta, myUserId, recipientId, text, attachments, verbose: true);
        if (messageId is null) return 1;

        Console.WriteLine($"\n=== send SELESAI (id: {messageId}) — jalankan 'pull' untuk verifikasi ===");
        return 0;
    }

    // ---------------- send-replies ----------------

    /// <summary>
    /// Baca antrian balasan DM di CRM (bi_twitters dengan bi_isdirectmessage = true,
    /// bi_direction = Out, statecode = 0 / Open, statuscode = Open, bi_replybody != null),
    /// kirim ke X, lalu tandai terkirim (isi bi_twittid, bi_createdat, kosongkan bi_replybody,
    /// serta set statecode = 1 / Completed &amp; statuscode = Sent).
    /// </summary>
    public static async Task<int> SendRepliesAsync(string[] args)
    {
        var dryRun = args.Contains("--dry-run");
        var verbose = args.Contains("--verbose");

        Console.WriteLine(dryRun
            ? "=== send-replies --dry-run: daftar kandidat antrian (tidak mengirim) ===\n"
            : "=== send-replies: kirim antrian balasan DM dari CRM -> X ===\n");

        var connStr = Environment.GetEnvironmentVariable("CRM_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            Console.Error.WriteLine("CRM_CONNECTION_STRING belum diisi — send-replies tidak bisa jalan.");
            return 1;
        }

        using var crm = new CrmWebApi(connStr);
        Console.WriteLine($"  endpoint: {crm.ApiBase}");
        var who = await crm.PingAsync();
        Console.WriteLine($"CRM OK (WhoAmI UserId {who})");

        var bodyAttr = Environment.GetEnvironmentVariable("CRM_REPLY_BODY_ATTRIBUTE");
        if (string.IsNullOrWhiteSpace(bodyAttr)) bodyAttr = "bi_replybody";

        var directionOut = Environment.GetEnvironmentVariable("CRM_REPLY_DIRECTION") ?? "157570001";
        var statusOpen = Environment.GetEnvironmentVariable("CRM_REPLY_STATUS_OPEN") ?? "1";
        var statusSent = Environment.GetEnvironmentVariable("CRM_REPLY_STATUS_SENT") ?? "157570001";
        var stateInactive = Environment.GetEnvironmentVariable("CRM_REPLY_STATUS_INACTIVE") ?? "1";

        var filter = $"bi_isdirectmessage eq true and bi_direction eq {directionOut} " +
                     $"and statecode eq 0 and statuscode eq {statusOpen} and bi_twittid eq null and {bodyAttr} ne null";
        var since = Environment.GetEnvironmentVariable("CRM_SEND_REPLIES_SINCE");
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateOnly.TryParse(since, out var cutoff))
            {
                Console.Error.WriteLine($"CRM_SEND_REPLIES_SINCE bukan tanggal valid (yyyy-MM-dd): '{since}'.");
                return 1;
            }
            filter += $" and createdon ge {cutoff:yyyy-MM-dd}T00:00:00Z";
            Console.WriteLine($"Cutoff tanggal: hanya record dibuat sejak {cutoff:yyyy-MM-dd}.");
        }

        var queue = await crm.RetrieveMultipleAsync(
            "bi_twitters", filter, $"activityid,{bodyAttr},bi_userid,bi_userscreenname,createdon");
        Console.WriteLine($"{queue.Count} balasan DM dalam antrian.\n");
        if (queue.Count == 0) return 0;

        if (dryRun)
        {
            foreach (var rec in queue)
            {
                var id = rec.GetProperty("activityid").GetString() ?? "";
                var text = rec.TryGetProperty(bodyAttr, out var b) ? b.GetString() : null;
                var screen = rec.TryGetProperty("bi_userscreenname", out var s) ? s.GetString()
                    : (rec.TryGetProperty("bi_userid", out var u) ? u.GetString() : "?");
                var created = rec.TryGetProperty("createdon", out var c) ? c.GetString() : "?";
                var notes = await crm.GetAnnotationsForRecordAsync(id);
                var attSuffix = notes.Count > 0 ? $" [+{notes.Count} lampiran Notes]" : "";
                Console.WriteLine($"  {id}  {created}  -> {screen}: {Truncate(text ?? "", 60)}{attSuffix}");
            }
            Console.WriteLine("\n(dry-run: tidak ada yang dikirim, CRM tidak diubah)");
            return 0;
        }

        using var chat = KeyStore.OpenChat(out var meta);
        if (meta.SigningKeyVersion is null)
        {
            Console.Error.WriteLine("SigningKeyVersion kosong — jalankan 'unlock-pin' atau 'register-keys' dulu.");
            return 1;
        }

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var myUserId = await client.GetAuthenticatedUserIdAsync();
        chat.SetIdentity(new SetIdentityParams(myUserId, meta.SigningKeyVersion));
        chat.SetCacheKeys(true);
        Console.WriteLine($"Login sebagai user id {myUserId} (key v{meta.SigningKeyVersion})\n");

        var sent = 0;
        var failed = 0;
        foreach (var rec in queue)
        {
            var id = rec.GetProperty("activityid").GetString() ?? "";
            var text = rec.TryGetProperty(bodyAttr, out var b) ? b.GetString() : null;
            var recipient = rec.TryGetProperty("bi_userid", out var u) ? u.GetString() : null;
            var screen = rec.TryGetProperty("bi_userscreenname", out var s) ? s.GetString() : recipient;

            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(recipient))
            {
                Console.WriteLine($"  [lewati] {id}: teks atau bi_userid kosong.");
                continue;
            }

            try
            {
                // Baca lampiran gambar/file dari Notes (annotations) pada record bi_twitter ini.
                var rawNotes = await crm.GetAnnotationsForRecordAsync(id);
                var attachments = rawNotes
                    .Select(n =>
                    {
                        var mime = !string.IsNullOrWhiteSpace(n.MimeType)
                            ? n.MimeType
                            : ChatXdkUtilities.DetectMimeType(n.Bytes.AsSpan());
                        if (string.IsNullOrWhiteSpace(mime))
                            mime = CrmChatSink.GuessMimeTypeFromFileName(n.FileName);
                        return new OutgoingAttachment(n.FileName, mime, n.Bytes);
                    })
                    .ToList();

                var messageId = await SendDmAsync(client, chat, meta, myUserId, recipient, text, attachments, verbose);
                if (messageId is null)
                {
                    failed++;
                    continue;
                }

                // Pertahankan bi_replybody (tidak dikosongkan); anti-kirim ulang dijaga
                // lewat bi_twittid + statecode = 1 + statuscode = Sent.
                var convHyphen = KeyStore.CanonicalConversationId(myUserId, recipient).Replace(':', '-');
                await crm.UpdateAsync("bi_twitters", id, new JsonObject
                {
                    ["bi_twittid"] = messageId,
                    ["bi_tweetlink"] = $"https://x.com/messages/{convHyphen}",
                    ["bi_createdat"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    ["description"] = text,
                });

                try
                {
                    await crm.UpdateAsync("bi_twitters", id, new JsonObject
                    {
                        ["statuscode"] = int.Parse(statusSent),
                        ["statecode"] = int.Parse(stateInactive),
                    });
                }
                catch (Exception stx)
                {
                    Console.WriteLine($"    [peringatan] status Sent gagal di-set: {stx.Message}");
                }

                sent++;
                var attInfo = attachments.Count > 0 ? $" [+{attachments.Count} lampiran]" : "";
                Console.WriteLine($"  [terkirim -> {screen}] {Truncate(text, 60)}{attInfo} (id {messageId})");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"  [gagal -> {screen}] {Truncate(text, 60)}\n    {ex.Message}");
            }
        }

        Console.WriteLine($"\n=== send-replies SELESAI: {sent} terkirim, {failed} gagal ===");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Proses 1 record bi_twitter DM secara langsung berdasarkan activityid (dipanggil oleh mode 'serve').
    /// Aman terhadap trigger ganda: memeriksa ulang statecode == 0, bi_direction == Out, dan bi_replybody != null.
    /// </summary>
    public static async Task<bool> ProcessSingleReplyAsync(CrmWebApi crm, string activityId, bool verbose = false)
    {
        var bodyAttr = Environment.GetEnvironmentVariable("CRM_REPLY_BODY_ATTRIBUTE");
        if (string.IsNullOrWhiteSpace(bodyAttr)) bodyAttr = "bi_replybody";

        var directionOut = int.Parse(Environment.GetEnvironmentVariable("CRM_REPLY_DIRECTION") ?? "157570001");
        var statusSent = int.Parse(Environment.GetEnvironmentVariable("CRM_REPLY_STATUS_SENT") ?? "157570001");
        var stateInactive = int.Parse(Environment.GetEnvironmentVariable("CRM_REPLY_STATUS_INACTIVE") ?? "1");

        using var doc = await crm.GetAsync(
            $"bi_twitters({activityId})?$select=activityid,{bodyAttr},bi_twittid,bi_userid,bi_userscreenname,bi_direction,statecode,bi_isdirectmessage");
        var rec = doc.RootElement;

        var state = rec.TryGetProperty("statecode", out var stEl) && stEl.TryGetInt32(out var sVal) ? sVal : -1;
        var dir = rec.TryGetProperty("bi_direction", out var dEl) && dEl.TryGetInt32(out var dVal) ? dVal : -1;
        var existingTwittId = rec.TryGetProperty("bi_twittid", out var twEl) && twEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? twEl.GetString() : null;
        var text = rec.TryGetProperty(bodyAttr, out var bEl) && bEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? bEl.GetString() : null;
        var recipient = rec.TryGetProperty("bi_userid", out var uEl) && uEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? uEl.GetString() : null;
        var screen = rec.TryGetProperty("bi_userscreenname", out var scEl) && scEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? scEl.GetString() : recipient;

        if (state != 0 || dir != directionOut || !string.IsNullOrWhiteSpace(existingTwittId) ||
            string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(recipient))
        {
            Console.WriteLine(
                $"  [serve/dm lewati] {activityId}: sudah terkirim atau tidak memenuhi syarat antrian " +
                $"(statecode={state} [harus 0], bi_direction={dir} [harus {directionOut}], " +
                $"bi_twittid={(string.IsNullOrWhiteSpace(existingTwittId) ? "<kosong>" : existingTwittId)}, " +
                $"{bodyAttr}={(string.IsNullOrWhiteSpace(text) ? "<kosong>" : $"len {text.Length}")}, " +
                $"bi_userid={(string.IsNullOrWhiteSpace(recipient) ? "<kosong>" : recipient)}).");
            return false;
        }

        using var chat = KeyStore.OpenChat(out var meta);
        if (meta.SigningKeyVersion is null)
            throw new InvalidOperationException("SigningKeyVersion kosong — jalankan 'unlock-pin' atau 'register-keys' dulu.");

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);
        var myUserId = await client.GetAuthenticatedUserIdAsync();
        chat.SetIdentity(new SetIdentityParams(myUserId, meta.SigningKeyVersion));
        chat.SetCacheKeys(true);

        var rawNotes = await crm.GetAnnotationsForRecordAsync(activityId);
        var attachments = rawNotes
            .Select(n =>
            {
                var mime = !string.IsNullOrWhiteSpace(n.MimeType)
                    ? n.MimeType
                    : ChatXdkUtilities.DetectMimeType(n.Bytes.AsSpan());
                if (string.IsNullOrWhiteSpace(mime))
                    mime = CrmChatSink.GuessMimeTypeFromFileName(n.FileName);
                return new OutgoingAttachment(n.FileName, mime, n.Bytes);
            })
            .ToList();

        var messageId = await SendDmAsync(client, chat, meta, myUserId, recipient, text, attachments, verbose);
        if (messageId is null) return false;

        var convHyphen = KeyStore.CanonicalConversationId(myUserId, recipient).Replace(':', '-');
        await crm.UpdateAsync("bi_twitters", activityId, new JsonObject
        {
            ["bi_twittid"] = messageId,
            ["bi_tweetlink"] = $"https://x.com/messages/{convHyphen}",
            ["bi_createdat"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["description"] = text,
        });
        try
        {
            await crm.UpdateAsync("bi_twitters", activityId, new JsonObject
            {
                ["statuscode"] = statusSent,
                ["statecode"] = stateInactive,
            });
        }
        catch (Exception stx)
        {
            Console.WriteLine($"    [peringatan] status Sent gagal di-set: {stx.Message}");
        }

        var attInfo = attachments.Count > 0 ? $" [+{attachments.Count} lampiran]" : "";
        Console.WriteLine($"  [serve/dm terkirim -> {screen}] {Truncate(text, 60)}{attInfo} (id {messageId})");
        return true;
    }

    // ---------------- Inti pengiriman DM (E2EE + fallback plaintext) ----------------

    /// <summary>
    /// Kirim DM terenkripsi (E2EE) beserta lampiran opsional bila penerima memiliki public key;
    /// otomatis fallback ke DM plaintext (+ upload /2/media/upload) bila penerima non-E2EE.
    /// </summary>
    public static async Task<string?> SendDmAsync(
        XApiChatClient client, XChat chat, KeyMeta meta, string myUserId,
        string recipientId, string text, IReadOnlyList<OutgoingAttachment>? attachments = null,
        bool verbose = false)
    {
        var publicKeys = new List<PublicKeyInput>();
        foreach (var uid in new[] { myUserId, recipientId })
        {
            var records = await client.GetPublicKeysAsync(uid);
            foreach (var r in records.OfType<JsonObject>())
            {
                var version = r["public_key_version"]?.GetValue<string>();
                var identity = r["public_key"]?.GetValue<string>();
                if (version is null || identity is null) continue;
                publicKeys.Add(new PublicKeyInput
                {
                    UserId = uid,
                    IdentityPublicKey = identity,
                    PublicKeyVersion = version,
                });
            }
        }

        // Pastikan kunci lokal kita selalu ikut dibungkus.
        if (!publicKeys.Any(k => k.UserId == myUserId && k.IdentityPublicKey == meta.IdentityPublic))
        {
            publicKeys.Add(new PublicKeyInput
            {
                UserId = myUserId,
                IdentityPublicKey = meta.IdentityPublic,
                PublicKeyVersion = meta.SigningKeyVersion!,
            });
        }

        // Penerima belum punya public key X Chat -> fallback DM plaintext.
        if (!publicKeys.Any(k => k.UserId == recipientId))
        {
            Console.WriteLine(
                $"  [plaintext fallback] Penerima {recipientId} tidak punya public key X Chat — mengirim via /2/dm_conversations.");
            List<string>? mediaIds = null;
            if (attachments is { Count: > 0 })
            {
                mediaIds = new List<string>(attachments.Count);
                foreach (var att in attachments)
                {
                    if (verbose) Console.WriteLine($"  Mengunggah media plaintext: {att.FileName} ({att.Bytes.Length}B, {att.MimeType})...");
                    var mid = await client.UploadPlaintextDmMediaAsync(att.Bytes, att.MimeType, att.FileName);
                    mediaIds.Add(mid);
                }
            }
            return await client.SendPlaintextDmAsync(recipientId, text, mediaIds);
        }

        if (verbose)
            Console.WriteLine($"  Key dibungkus untuk: {string.Join(", ",
                publicKeys.Select(k => $"{(k.UserId.Length > 6 ? "..." + k.UserId[^6..] : k.UserId)}#v{k.PublicKeyVersion}"))}");

        var change = chat.PrepareConversationKeyChange(new ConversationKeyChangeParams(publicKeys)
        {
            MemberIds = new List<string> { myUserId, recipientId },
            SenderId = myUserId,
            SigningKeyVersion = meta.SigningKeyVersion!,
        });
        var hyphenId = change.ConversationId.Replace(':', '-');
        if (verbose) Console.WriteLine($"  Percakapan: {hyphenId} (key v{change.ConversationKeyVersion})");

        await client.AddConversationKeysAsync(hyphenId, change);
        if (verbose) Console.WriteLine("  Key change terkirim (POST /keys).");

        // Simpan conversation key (DPAPI) segera setelah POST /keys sukses.
        KeyStore.StoreConversationKey(change.ConversationId, change.ConversationKeyVersion, change.ConversationKey);
        if (verbose) Console.WriteLine("  Conversation key tersimpan ke KeyStore (DPAPI).");

        // Jika ada lampiran: 1) initialize upload untuk dapat media_hash_key,
        // 2) EncryptMessage dengan AttachmentDescriptor (menghasilkan payload.EventB64),
        // 3) EncryptFile memakai payload.EventB64 lalu append + finalize,
        // 4) kirim POST /messages.
        var preparedUploads = new List<(OutgoingAttachment Att, string SessionId, string MediaHashKey, string ConvId, long ExpectedEncSize)>();
        List<AttachmentDescriptor>? descriptors = null;
        if (attachments is { Count: > 0 })
        {
            descriptors = new List<AttachmentDescriptor>(attachments.Count);
            foreach (var att in attachments)
            {
                var encSize = (long)ChatXdkUtilities.EncryptedFileSize((ulong)att.Bytes.Length);
                var (sessionId, mediaHashKey, uploadConvId) = await client.InitializeChatMediaUploadAsync(hyphenId, encSize);
                preparedUploads.Add((att, sessionId, mediaHashKey, uploadConvId, encSize));

                long width = 0, height = 0;
                try
                {
                    if (ChatXdkUtilities.DetectImageDimensions(att.Bytes.AsSpan()) is { } dims)
                    {
                        width = dims.Width;
                        height = dims.Height;
                    }
                }
                catch { /* bukan gambar / dimensi tidak terdeteksi */ }

                int? mediaType = att.MimeType.ToLowerInvariant() switch
                {
                    "image/gif" => 3,
                    var m when m.StartsWith("image/", StringComparison.Ordinal) => 1,
                    var m when m.StartsWith("video/", StringComparison.Ordinal) => 2,
                    var m when m.StartsWith("audio/", StringComparison.Ordinal) => 4,
                    _ => null,
                };

                descriptors.Add(AttachmentDescriptor.Media(
                    mediaHashKey, width, height, att.Bytes.Length, att.FileName, mediaType, durationMillis: null));
                if (verbose)
                    Console.WriteLine($"  Init upload E2EE: {att.FileName} ({att.Bytes.Length}B -> enc {encSize}B, {width}x{height}, session {sessionId}, conv {uploadConvId})");
            }
        }

        var payload = chat.EncryptMessage(new EncryptMessageParams(change.ConversationId, text)
        {
            ConversationKey = change.ConversationKey,
            ConversationKeyVersion = change.ConversationKeyVersion,
            SenderId = myUserId,
            SigningKeyVersion = meta.SigningKeyVersion!,
            Attachments = descriptors,
        });

        foreach (var (att, sessionId, mediaHashKey, uploadConvId, expectedEncSize) in preparedUploads)
        {
            var encryptedBytes = chat.EncryptFile(new EncryptFileParams(att.Bytes, payload.EventB64, mediaHashKey)
            {
                ConversationKey = change.ConversationKey,
                ConversationKeyVersion = change.ConversationKeyVersion,
            });
            if (verbose)
                Console.WriteLine($"  Mengunggah E2EE: {att.FileName} (actual {encryptedBytes.Length}B vs expected {expectedEncSize}B)...");
            await client.UploadEncryptedChatMediaChunksAsync(uploadConvId, sessionId, mediaHashKey, encryptedBytes);
            if (verbose)
                Console.WriteLine($"  Upload E2EE selesai: {att.FileName} ({encryptedBytes.Length}B terenkripsi)");
        }

        await client.SendChatMessageAsync(hyphenId, payload);
        return payload.MessageId;
    }

    private static string Truncate(string s, int max) => s.Length >= max ? s.Substring(0, max) : s;
}
