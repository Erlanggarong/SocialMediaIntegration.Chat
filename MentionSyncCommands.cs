// ============================================================
// MentionSyncCommands — sinkronisasi & balasan mention publik X <-> Dynamics CRM
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Perintah:
//   sync-mentions [--dry-run] [--full] [--verbose]
//       Tarik mention publik (GET /2/users/{id}/mentions) -> CRM bi_twitter.
//   send-mention-replies [--dry-run] [--verbose]
//       Kirim antrian balasan mention dari CRM (bi_isdirectmessage = false,
//       bi_replybody terisi, + lampiran Notes opsional) via POST /2/tweets.
//   reply-mention <tweetId> [pesan] [--file <path>]
//       Uji balas satu tweet/mention langsung dari CLI.
// ============================================================

using System.Text.Json.Nodes;
using ChatXdk;

namespace SocialMediaIntegration.Chat;

internal static class MentionSyncCommands
{
    private const string DefaultMentionConfigName = "X Mention Last Sync";
    private const string LegacyTwitterIdConfigName = "BI Twitter Id";
    private static readonly TimeSpan WatermarkSafetyBuffer = TimeSpan.FromMinutes(2);

    public static async Task<int> SyncAsync(string[] args)
    {
        var dryRun = args.Contains("--dry-run");
        var fullSync = args.Contains("--full");
        var verbose = args.Contains("--verbose");

        Console.WriteLine(dryRun
            ? "=== sync-mentions --dry-run: simulasi tarik mention X -> CRM (tidak menulis) ===\n"
            : "=== sync-mentions: tarik mention publik X -> CRM bi_twitter ===\n");

        var connStr = Environment.GetEnvironmentVariable("CRM_CONNECTION_STRING");
        CrmWebApi? crm = null;
        if (!dryRun && !string.IsNullOrWhiteSpace(connStr))
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
            var reason = dryRun ? "flag --dry-run aktif" : "CRM_CONNECTION_STRING tidak diisi";
            Console.WriteLine($"Mode DRY-RUN ({reason}) — tidak ada yang ditulis ke CRM.\n");
        }

        using var crmScope = crm;

        var wmConfigName = Environment.GetEnvironmentVariable("CRM_MENTION_CONFIG_NAME");
        if (string.IsNullOrWhiteSpace(wmConfigName)) wmConfigName = DefaultMentionConfigName;

        string? wmConfigId = null;
        DateTime? lastSyncUtc = null;
        DateTime? startTimeUtc = null;
        string? configuredUserId = null;
        var syncStartedUtc = DateTime.UtcNow;

        if (crm is not null)
        {
            // Ikuti pola XApiGetMentionList: cek 'BI Twitter Id' di new_configuration (fallback ke /2/users/me).
            try
            {
                var (_, uidVal) = await crm.GetConfigurationAsync(LegacyTwitterIdConfigName);
                if (!string.IsNullOrWhiteSpace(uidVal))
                    configuredUserId = uidVal.Trim();
            }
            catch (Exception ex)
            {
                if (verbose)
                    Console.WriteLine($"  [info] gagal membaca '{LegacyTwitterIdConfigName}': {ex.Message}");
            }

            if (!fullSync)
            {
                try
                {
                    var (id, rawVal) = await crm.GetConfigurationAsync(wmConfigName);
                    wmConfigId = id;
                    if (CrmUtility.TryParseXTimestamp(rawVal, out var parsed))
                    {
                        lastSyncUtc = parsed;
                        startTimeUtc = parsed - WatermarkSafetyBuffer;
                        Console.WriteLine(
                            $"Watermark '{wmConfigName}': {parsed:yyyy-MM-ddTHH:mm:ssZ} " +
                            $"(start_time dgn buffer {WatermarkSafetyBuffer.TotalMinutes:0}m: {startTimeUtc:yyyy-MM-ddTHH:mm:ssZ})\n");
                    }
                    else
                    {
                        Console.WriteLine($"Watermark '{wmConfigName}' belum terisi — sinkronisasi awal.\n");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [peringatan] gagal membaca watermark '{wmConfigName}': {ex.Message}\n");
                }
            }
            else
            {
                Console.WriteLine($"Mode --full aktif — watermark '{wmConfigName}' diabaikan.\n");
            }
        }

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var targetUserId = !string.IsNullOrWhiteSpace(configuredUserId)
            ? configuredUserId
            : await client.GetAuthenticatedUserIdAsync();
        Console.WriteLine(
            $"Target user id: {targetUserId}" +
            (!string.IsNullOrWhiteSpace(configuredUserId) ? $" (dari '{LegacyTwitterIdConfigName}')" : " (dari /2/users/me)"));

        var maxPages = 5;
        var maxPagesEnv = Environment.GetEnvironmentVariable("X_MENTIONS_MAX_PAGES");
        if (!string.IsNullOrWhiteSpace(maxPagesEnv) && int.TryParse(maxPagesEnv, out var parsedPages))
            maxPages = parsedPages;

        var (tweets, usersById, mediaByKey) = await client.GetMentionsAsync(targetUserId, maxPages, startTimeUtc);
        Console.WriteLine($"{tweets.Count} mention diterima dari X API.\n");

        var created = 0;
        var skipped = 0;
        var failed = 0;
        DateTime? maxProcessedUtc = null;

        foreach (var tweet in tweets)
        {
            var mention = CrmChatSink.FromMentionTweet(tweet, usersById, mediaByKey);
            if (mention is null) continue;

            if (crm is null)
            {
                var mediaInfo = mention.MediaUrls.Count > 0 ? $" [+{mention.MediaUrls.Count} media]" : "";
                Console.WriteLine($"  [dry-run] @{mention.AuthorScreenName}: {Truncate(mention.Text, 80)}{mediaInfo}");
                continue;
            }

            try
            {
                if (await CrmChatSink.SaveMentionAsync(crm, mention))
                    created++;
                else
                    skipped++;

                if (mention.CreatedUtc is { } ts && (maxProcessedUtc is null || ts > maxProcessedUtc.Value))
                    maxProcessedUtc = ts;
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"  Gagal simpan mention {mention.TweetId}: {ex.Message}");
            }
        }

        if (crm is not null)
        {
            if (failed > 0)
            {
                Console.Error.WriteLine(
                    $"  [watermark] '{wmConfigName}' tidak dimajukan karena ada {failed} mention gagal disimpan.");
            }
            else
            {
                var newWatermark = maxProcessedUtc is { } maxTs && maxTs > syncStartedUtc
                    ? maxTs
                    : syncStartedUtc;
                if (lastSyncUtc is { } prev && newWatermark < prev)
                    newWatermark = prev;

                var wmText = newWatermark.ToString("yyyy-MM-ddTHH:mm:ssZ");
                try
                {
                    await crm.UpsertConfigurationAsync(wmConfigName, wmText, wmConfigId);
                    Console.WriteLine($"  [watermark] '{wmConfigName}' diperbarui -> {wmText}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  [peringatan] gagal memperbarui watermark '{wmConfigName}': {ex.Message}");
                }
            }
        }

        Console.WriteLine(
            $"\n=== sync-mentions SELESAI: {created} record baru, {skipped} duplikat dilewati" +
            (failed > 0 ? $", {failed} gagal" : "") + " ===");
        return failed > 0 ? 1 : 0;
    }

    // ---------------- reply-mention (balas manual dari CLI) ----------------

    /// <summary>Balas satu tweet/mention langsung dari CLI: reply-mention &lt;tweetId&gt; [pesan] [--file &lt;path&gt;].</summary>
    public static async Task<int> ReplyAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Argumen kurang. Pakai: reply-mention <tweetId> [pesan] [--file <path>]");
            return 2;
        }

        var tweetId = args[1];
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
            Console.Error.WriteLine("Isi balasan atau minimal satu --file wajib diberikan.");
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

        Console.WriteLine($"=== reply-mention: balas tweet {tweetId} ===\n");

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var myUserId = await client.GetAuthenticatedUserIdAsync();
        Console.WriteLine($"Login sebagai user id {myUserId}");

        var newTweetId = await SendMentionReplyCoreAsync(client, tweetId, text, attachments, verbose: true);
        Console.WriteLine($"\n=== reply-mention SELESAI (tweet id: {newTweetId}) ===");
        Console.WriteLine($"Link: https://x.com/i/status/{newTweetId}");
        return 0;
    }

    // ---------------- send-mention-replies (antrian balasan mention CRM -> X) ----------------

    /// <summary>
    /// Baca antrian balasan mention di CRM (bi_twitters dengan bi_isdirectmessage = false,
    /// bi_direction = Out, statecode = 0 / Open, statuscode = Open, bi_replybody != null),
    /// kirim sebagai reply tweet (POST /2/tweets, termasuk lampiran Notes bila ada),
    /// lalu tandai terkirim (bi_twittid, bi_tweetlink, bi_createdat, kosongkan bi_replybody,
    /// set statecode = 1 &amp; statuscode = Sent).
    /// </summary>
    public static async Task<int> SendRepliesAsync(string[] args)
    {
        var dryRun = args.Contains("--dry-run");
        var verbose = args.Contains("--verbose");

        Console.WriteLine(dryRun
            ? "=== send-mention-replies --dry-run: daftar kandidat antrian mention (tidak mengirim) ===\n"
            : "=== send-mention-replies: kirim antrian balasan mention dari CRM -> X ===\n");

        var connStr = Environment.GetEnvironmentVariable("CRM_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            Console.Error.WriteLine("CRM_CONNECTION_STRING belum diisi — send-mention-replies tidak bisa jalan.");
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

        var filter = $"bi_isdirectmessage eq false and bi_direction eq {directionOut} " +
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
            "bi_twitters", filter,
            $"activityid,{bodyAttr},bi_inreplystatusid,bi_userscreenname,bi_inreplytoscreenname,createdon");
        Console.WriteLine($"{queue.Count} balasan mention dalam antrian.\n");
        if (queue.Count == 0) return 0;

        if (dryRun)
        {
            foreach (var rec in queue)
            {
                var id = rec.GetProperty("activityid").GetString() ?? "";
                var text = rec.TryGetProperty(bodyAttr, out var b) ? b.GetString() : null;
                var targetTweet = rec.TryGetProperty("bi_inreplystatusid", out var rt) ? rt.GetString() : null;
                var screen = rec.TryGetProperty("bi_userscreenname", out var s) ? s.GetString()
                    : (rec.TryGetProperty("bi_inreplytoscreenname", out var rs) ? rs.GetString() : "?");
                var created = rec.TryGetProperty("createdon", out var c) ? c.GetString() : "?";
                var notes = await crm.GetAnnotationsForRecordAsync(id);
                var attSuffix = notes.Count > 0 ? $" [+{notes.Count} lampiran Notes]" : "";
                var replyInfo = string.IsNullOrWhiteSpace(targetTweet) ? "(tweet baru)" : $"reply->{targetTweet}";
                Console.WriteLine($"  {id}  {created}  {replyInfo} @{screen}: {Truncate(text ?? "", 60)}{attSuffix}");
            }
            Console.WriteLine("\n(dry-run: tidak ada yang dikirim, CRM tidak diubah)");
            return 0;
        }

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

        var myUserId = await client.GetAuthenticatedUserIdAsync();
        Console.WriteLine($"Login sebagai user id {myUserId}\n");

        var sent = 0;
        var failed = 0;
        foreach (var rec in queue)
        {
            var id = rec.GetProperty("activityid").GetString() ?? "";
            var text = rec.TryGetProperty(bodyAttr, out var b) ? b.GetString() : null;
            var targetTweet = rec.TryGetProperty("bi_inreplystatusid", out var rt) ? rt.GetString() : null;
            var screen = rec.TryGetProperty("bi_userscreenname", out var s) ? s.GetString()
                : (rec.TryGetProperty("bi_inreplytoscreenname", out var rs) ? rs.GetString() : "?");

            if (string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine($"  [lewati] {id}: teks balasan kosong.");
                continue;
            }

            try
            {
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

                var newTweetId = await SendMentionReplyCoreAsync(client, targetTweet, text, attachments, verbose);

                await crm.UpdateAsync("bi_twitters", id, new JsonObject
                {
                    ["bi_twittid"] = newTweetId,
                    ["bi_tweetlink"] = $"https://x.com/i/status/{newTweetId}",
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
                Console.WriteLine($"  [terkirim -> @{screen}] {Truncate(text, 60)}{attInfo} (tweet {newTweetId})");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"  [gagal -> @{screen}] {Truncate(text, 60)}\n    {ex.Message}");
            }
        }

        Console.WriteLine($"\n=== send-mention-replies SELESAI: {sent} terkirim, {failed} gagal ===");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Proses 1 record bi_twitter balasan mention secara langsung berdasarkan activityid (dipanggil oleh mode 'serve').
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
            $"bi_twitters({activityId})?$select=activityid,{bodyAttr},bi_twittid,bi_inreplystatusid,bi_userscreenname,bi_inreplytoscreenname,bi_direction,statecode");
        var rec = doc.RootElement;

        var state = rec.TryGetProperty("statecode", out var stEl) && stEl.TryGetInt32(out var sVal) ? sVal : -1;
        var dir = rec.TryGetProperty("bi_direction", out var dEl) && dEl.TryGetInt32(out var dVal) ? dVal : -1;
        var existingTwittId = rec.TryGetProperty("bi_twittid", out var twEl) && twEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? twEl.GetString() : null;
        var text = rec.TryGetProperty(bodyAttr, out var bEl) && bEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? bEl.GetString() : null;
        var targetTweet = rec.TryGetProperty("bi_inreplystatusid", out var rtEl) && rtEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? rtEl.GetString() : null;
        var screen = rec.TryGetProperty("bi_userscreenname", out var scEl) && scEl.ValueKind == System.Text.Json.JsonValueKind.String
            ? scEl.GetString()
            : (rec.TryGetProperty("bi_inreplytoscreenname", out var rsEl) && rsEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? rsEl.GetString() : "?");

        if (state != 0 || dir != directionOut || !string.IsNullOrWhiteSpace(existingTwittId) || string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine(
                $"  [serve/mention lewati] {activityId}: sudah terkirim atau tidak memenuhi syarat antrian " +
                $"(statecode={state} [harus 0], bi_direction={dir} [harus {directionOut}], " +
                $"bi_twittid={(string.IsNullOrWhiteSpace(existingTwittId) ? "<kosong>" : existingTwittId)}, " +
                $"{bodyAttr}={(string.IsNullOrWhiteSpace(text) ? "<kosong>" : $"len {text.Length}")}, " +
                $"bi_inreplystatusid={(string.IsNullOrWhiteSpace(targetTweet) ? "<kosong>" : targetTweet)}).");
            return false;
        }

        var cred = XCredentials.FromEnvironmentOrPrompt();
        using var client = new XApiChatClient(cred);

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

        var newTweetId = await SendMentionReplyCoreAsync(client, targetTweet, text, attachments, verbose);

        await crm.UpdateAsync("bi_twitters", activityId, new JsonObject
        {
            ["bi_twittid"] = newTweetId,
            ["bi_tweetlink"] = $"https://x.com/i/status/{newTweetId}",
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
        Console.WriteLine($"  [serve/mention terkirim -> @{screen}] {Truncate(text, 60)}{attInfo} (tweet {newTweetId})");
        return true;
    }

    private static async Task<string> SendMentionReplyCoreAsync(
        XApiChatClient client, string? inReplyToTweetId, string text,
        IReadOnlyList<OutgoingAttachment>? attachments, bool verbose)
    {
        List<string>? mediaIds = null;
        if (attachments is { Count: > 0 })
        {
            // Batas X API: maksimal 4 gambar per tweet.
            var toUpload = attachments.Take(4).ToList();
            mediaIds = new List<string>(toUpload.Count);
            foreach (var att in toUpload)
            {
                if (verbose)
                    Console.WriteLine($"  Mengunggah media tweet: {att.FileName} ({att.Bytes.Length}B, {att.MimeType})...");
                var mid = await client.UploadTweetMediaAsync(att.Bytes, att.MimeType, att.FileName);
                mediaIds.Add(mid);
            }
        }

        return await client.PostTweetAsync(text, inReplyToTweetId, mediaIds);
    }

    private static string Truncate(string s, int max) => s.Length >= max ? s.Substring(0, max) : s;
}
