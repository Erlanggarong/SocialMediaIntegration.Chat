// ============================================================
// CrmChatSink — mapping pesan DM X (E2EE XDK & Plaintext /2/dm_events)
// menjadi activity bi_twitter di Dynamics CRM.
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Skema field mengikuti XApiGetDM (pipeline lama) 1:1, sehingga record dari
// kanal terenkripsi maupun plaintext tampil konsisten di CRM:
//   bi_twittid, bi_isdirectmessage, bi_createdat, description,
//   overriddencreatedon, bi_username, bi_userscreenname, bi_userid,
//   bi_direction (157570000 Incoming / 157570001 Outgoing), subject,
//   bi_isencrypted, bi_precontact, bi_twitter_activity_parties (From/To), bi_tweetlink.
//
// Lampiran media (E2EE maupun plaintext) diunggah ke entity annotation (Notes)
// yang terhubung ke record bi_twitter terkait.
// ============================================================

using System.Text.Json;
using System.Text.Json.Nodes;

namespace SocialMediaIntegration.Chat;

/// <summary>Metadata &amp; payload lampiran media pada pesan DM (E2EE maupun plaintext).</summary>
public sealed class ChatAttachment
{
    /// <summary>Hash key untuk download &amp; dekripsi media X Chat E2EE (/2/chat/media/{convId}/{hashKey}).</summary>
    public string? MediaHashKey { get; init; }
    /// <summary>URL media langsung untuk DM plaintext / legacy.</summary>
    public string? LegacyUrl { get; init; }
    public string? FileName { get; set; }
    public string? MediaType { get; init; }
    public long? FileSizeBytes { get; init; }
    /// <summary>Byte file siap unggah ke CRM (sudah didekripsi untuk E2EE).</summary>
    public byte[]? DecryptedBytes { get; set; }
    public string? MimeType { get; set; }
}

/// <summary>DTO seragam untuk pesan DM hasil dekripsi XDK maupun DM plaintext /2/dm_events.</summary>
public sealed class ChatMessage
{
    public required string MessageId { get; init; }
    public required string ConversationId { get; init; }
    public required string SenderId { get; init; }
    public required string Text { get; init; }
    public required DateTime CreatedUtc { get; init; }
    /// <summary>true = DM terenkripsi (X Chat / XDK), false = DM plaintext klasik (/2/dm_events).</summary>
    public bool IsEncrypted { get; init; } = true;
    /// <summary>Payload event terenkripsi base64 asli (diperlukan oleh Chat.DecryptFile).</summary>
    public string? RawEventB64 { get; init; }
    /// <summary>Conversation keys lokal bila event dipulihkan lewat KeyStore (diperlukan oleh Chat.DecryptFile).</summary>
    public IReadOnlyDictionary<string, byte[]>? RecoveredConversationKeys { get; init; }
    public IReadOnlyList<ChatAttachment> Attachments { get; init; } = Array.Empty<ChatAttachment>();
}

/// <summary>DTO untuk mention publik X (GET /2/users/{id}/mentions) -> activity bi_twitter.</summary>
public sealed class MentionItem
{
    public required string TweetId { get; init; }
    public required string Text { get; init; }
    public required string AuthorId { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorScreenName { get; init; }
    public string? CreatedAtRaw { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public string? InReplyToStatusId { get; init; }
    public IReadOnlyList<string> MediaUrls { get; init; } = Array.Empty<string>();
}

public static class CrmChatSink
{
    /// <summary>
    /// Simpan satu pesan DM sebagai activity bi_twitter (dedupe via bi_twittid).
    /// Mengembalikan true bila record baru dibuat.
    /// </summary>
    public static async Task<bool> SaveMessageAsync(
        CrmWebApi crm, ChatMessage msg, string myUserId,
        string counterpartName, string counterpartScreenName)
    {
        // Dedupe: lewati jika activity dengan message id ini sudah ada.
        var dup = await crm.RetrieveFirstAsync("bi_twitters",
            $"bi_twittid eq '{CrmWebApi.Esc(msg.MessageId)}'", "activityid", "createdon desc");
        if (dup is not null)
            return false;

        var counterpartId = msg.SenderId == myUserId
            ? InferCounterpart(msg.ConversationId, myUserId)   // pesan outgoing: lawan = participant lain
            : msg.SenderId;
        if (string.IsNullOrEmpty(counterpartId)) counterpartId = msg.SenderId;

        bool isIncoming = msg.SenderId != myUserId;
        string content = msg.Text;

        // Pre-contact pihak lawan (port GetPrecontact, bi_source = 3 untuk X).
        var (precontactId, contactId) = await crm.GetOrCreatePrecontactAsync(
            counterpartScreenName, counterpartId, counterpartName);

        var body = new JsonObject
        {
            ["bi_twittid"] = msg.MessageId,
            ["bi_isdirectmessage"] = true,
            ["bi_createdat"] = msg.CreatedUtc.ToString("o"),
            ["description"] = content,
            ["overriddencreatedon"] = msg.CreatedUtc.ToString("o"),
            ["bi_username"] = counterpartName,
            ["bi_userscreenname"] = counterpartScreenName,
            ["bi_userid"] = counterpartId,
            ["bi_direction"] = isIncoming ? 157570000 : 157570001,
            ["subject"] = (isIncoming ? "Incoming Direct Message - " : "Outgoing Direct Message - ")
                          + Truncate(content, 124),
            ["bi_isencrypted"] = msg.IsEncrypted,
            // Link percakapan diseragamkan ke format hyphen (min-max) agar konsisten E2EE & plaintext.
            ["bi_tweetlink"] = $"https://x.com/messages/{msg.ConversationId.Replace(':', '-')}",

        };

        // Pre-contact: pakai NAMA NAVIGATION PROPERTY dari metadata organisasi
        // (nama relasi ManyToOne, bukan nama atribut lookup) — diresolusi dinamis
        // karena nama relasi bisa beda antar organisasi.
        var preNav = await crm.GetLookupNavigationNameAsync("bi_twitter", "bi_precontact");
        if (preNav is not null)
            body[$"{preNav}@odata.bind"] = $"/bi_precontacts({precontactId})";
        else
            Console.Error.WriteLine("  [peringatan] relasi bi_twitter->bi_precontact tidak ditemukan di metadata; " +
                                    "record dibuat tanpa link precontact.");

        if (contactId is not null)
        {
            // Gunakan collection navigation property "{entity_logical_name}_activity_parties"
            // dan bedakan From (1) / To (2) melalui "participationtypemask".
            var parties = new JsonArray
    {
        new JsonObject
        {
            ["partyid_contact@odata.bind"] = $"/contacts({contactId})",
            ["participationtypemask"] = isIncoming ? 1 : 2 // 1 = From (Sender), 2 = To (Recipient)
        }
    };

            body["bi_twitter_activity_parties"] = parties;
        }

        var activityId = await crm.CreateAsync("bi_twitters", body);

        // Unggah lampiran yang sudah diunduh/didekripsi sebagai Note/Attachment (annotation).
        foreach (var att in msg.Attachments)
        {
            if (att.DecryptedBytes is not { Length: > 0 })
                continue;
            var fileName = SanitizeFileName(att.FileName, att.MimeType, att.MediaType);
            var mime = string.IsNullOrWhiteSpace(att.MimeType)
                ? GuessMimeTypeFromFileName(fileName, att.MediaType)
                : att.MimeType;
            try
            {
                await crm.CreateAnnotationAsync(
                    "bi_twitter", "bi_twitters", activityId,
                    fileName, mime, att.DecryptedBytes, $"Attachment - {fileName}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [peringatan] lampiran '{fileName}' gagal diunggah ke CRM ({activityId}): {ex.Message}");
            }
        }

        return true;
    }

    /// <summary>
    /// Simpan satu mention publik (GET /2/users/{id}/mentions) sebagai activity bi_twitter
    /// (skema 1:1 mengikuti XApiGetMentionList.cs; dedupe via bi_twittid).
    /// Mengembalikan true bila record baru dibuat.
    /// </summary>
    public static async Task<bool> SaveMentionAsync(CrmWebApi crm, MentionItem m)
    {
        var dup = await crm.RetrieveFirstAsync("bi_twitters",
            $"bi_twittid eq '{CrmWebApi.Esc(m.TweetId)}'", "activityid", "createdon desc");
        if (dup is not null)
            return false;

        var screenName = string.IsNullOrWhiteSpace(m.AuthorScreenName) ? m.AuthorId : m.AuthorScreenName;
        var authorName = string.IsNullOrWhiteSpace(m.AuthorName) ? m.AuthorId : m.AuthorName;

        var (precontactId, contactId) = await crm.GetOrCreatePrecontactAsync(
            screenName, m.AuthorId, authorName);

        var body = new JsonObject
        {
            ["subject"] = Truncate(m.Text, 150),
            ["bi_twittid"] = m.TweetId,
            ["bi_createdat"] = m.CreatedAtRaw ?? (m.CreatedUtc?.ToString("o") ?? ""),
            ["description"] = m.Text,
            ["bi_direction"] = 157570000, // Incoming
            ["bi_isdirectmessage"] = false,
            ["bi_username"] = authorName,
            ["bi_userid"] = m.AuthorId,
            ["bi_userscreenname"] = screenName,
            ["bi_tweetlink"] = $"https://x.com/{screenName}/status/{m.TweetId}",
        };

        if (m.CreatedUtc is { } createdUtc)
            body["overriddencreatedon"] = createdUtc.ToString("o");

        if (!string.IsNullOrEmpty(m.InReplyToStatusId))
        {
            body["bi_inreplystatusid"] = m.InReplyToStatusId;
            body["bi_inreplytoscreenname"] = screenName;
            body["bi_inreplytouseridstr"] = m.AuthorId;
        }

        for (var i = 0; i < m.MediaUrls.Count && i < 4; i++)
        {
            var idx = i + 1;
            body[$"bi_media{idx}"] = m.MediaUrls[i];
            body[$"bi_medialink{idx}"] = m.MediaUrls[i];
        }

        var preNav = await crm.GetLookupNavigationNameAsync("bi_twitter", "bi_precontact");
        if (preNav is not null)
            body[$"{preNav}@odata.bind"] = $"/bi_precontacts({precontactId})";

        if (contactId is not null)
        {
            body["bi_twitter_activity_parties"] = new JsonArray
            {
                new JsonObject
                {
                    ["partyid_contact@odata.bind"] = $"/contacts({contactId})",
                    ["participationtypemask"] = 1, // From (Sender)
                }
            };
        }

        await crm.CreateAsync("bi_twitters", body);
        return true;
    }

    /// <summary>Konversi JsonObject tweet v2 dari GET /2/users/{id}/mentions menjadi MentionItem.</summary>
    public static MentionItem? FromMentionTweet(
        JsonObject tweet,
        IReadOnlyDictionary<string, JsonObject> usersById,
        IReadOnlyDictionary<string, JsonObject> mediaByKey)
    {
        var tweetId = tweet["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(tweetId))
            return null;

        var text = tweet["text"]?.GetValue<string>() ?? "";
        var authorId = tweet["author_id"]?.GetValue<string>() ?? "";
        var authorName = authorId;
        var authorScreen = authorId;
        if (!string.IsNullOrEmpty(authorId) && usersById.TryGetValue(authorId, out var author))
        {
            authorName = author["name"]?.GetValue<string>() ?? authorId;
            authorScreen = author["username"]?.GetValue<string>() ?? authorId;
        }

        string? replyToId = null;
        if (tweet["referenced_tweets"] is JsonArray refs)
        {
            foreach (var r in refs.OfType<JsonObject>())
            {
                if (r["type"]?.GetValue<string>() == "replied_to")
                {
                    replyToId = r["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(replyToId)) break;
                }
            }
        }

        var mediaUrls = new List<string>();
        if (tweet["attachments"]?["media_keys"] is JsonArray keys && mediaByKey.Count > 0)
        {
            foreach (var kNode in keys)
            {
                if (mediaUrls.Count >= 4) break;
                var key = kNode?.GetValue<string>();
                if (key is null || !mediaByKey.TryGetValue(key, out var media)) continue;
                var url = media["url"]?.GetValue<string>() ?? media["preview_image_url"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(url))
                    mediaUrls.Add(url);
            }
        }

        var createdRaw = tweet["created_at"]?.GetValue<string>();
        DateTime? createdUtc = CrmUtility.TryParseXTimestamp(createdRaw, out var parsed) ? parsed : null;

        return new MentionItem
        {
            TweetId = tweetId,
            Text = text,
            AuthorId = authorId,
            AuthorName = authorName,
            AuthorScreenName = authorScreen,
            CreatedAtRaw = createdRaw,
            CreatedUtc = createdUtc,
            InReplyToStatusId = replyToId,
            MediaUrls = mediaUrls,
        };
    }

    /// <summary>Ekstrak JsonElement event Message terdekripsi (XDK) menjadi ChatMessage (teks &amp;/atau lampiran).</summary>
    public static ChatMessage? FromEncryptedEvent(
        JsonElement evt, string? rawEventB64 = null,
        IReadOnlyDictionary<string, byte[]>? recoveredConversationKeys = null)
    {
        if (!evt.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "Message")
            return null;

        string? text = null;
        if (evt.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("text", out var textEl) &&
            textEl.ValueKind == JsonValueKind.String)
        {
            text = textEl.GetString();
        }

        var attachments = ExtractEncryptedAttachments(evt);
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
            return null;

        var finalText = string.IsNullOrWhiteSpace(text)
            ? "[Attachment] " + string.Join(", ", attachments.Select(a => a.FileName ?? a.MediaType ?? "media"))
            : text;

        return new ChatMessage
        {
            MessageId = evt.GetProperty("id").GetString() ?? "",
            ConversationId = evt.GetProperty("conversation_id").GetString() ?? "",
            SenderId = evt.GetProperty("sender_id").GetString() ?? "",
            Text = finalText,
            CreatedUtc = evt.TryGetProperty("created_at_msec", out var t) && t.TryGetInt64(out var ms)
                ? CrmUtility.FromEpochMsec(ms)
                : DateTime.UtcNow,
            IsEncrypted = true,
            RawEventB64 = rawEventB64,
            RecoveredConversationKeys = recoveredConversationKeys,
            Attachments = attachments,
        };
    }

    private static List<ChatAttachment> ExtractEncryptedAttachments(JsonElement evt)
    {
        var list = new List<ChatAttachment>();
        var seenHashes = new HashSet<string>(StringComparer.Ordinal);

        void TryAddMediaObj(JsonElement m)
        {
            if (m.ValueKind != JsonValueKind.Object) return;
            var hashKey = m.TryGetProperty("media_hash_key", out var hk) && hk.ValueKind == JsonValueKind.String
                ? hk.GetString() : null;
            var legacyUrl = m.TryGetProperty("legacy_media_url_https", out var lu) && lu.ValueKind == JsonValueKind.String
                ? lu.GetString() : null;
            if (string.IsNullOrEmpty(hashKey) && string.IsNullOrEmpty(legacyUrl))
                return;
            if (!string.IsNullOrEmpty(hashKey) && !seenHashes.Add(hashKey))
                return;

            var fileName = m.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString() : null;
            var mediaType = m.TryGetProperty("media_type", out var mt) && mt.ValueKind == JsonValueKind.String
                ? mt.GetString()
                : (m.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null);
            long? size = m.TryGetProperty("filesize_bytes", out var fs) && fs.TryGetInt64(out var sz) ? sz : null;

            list.Add(new ChatAttachment
            {
                MediaHashKey = hashKey,
                LegacyUrl = legacyUrl,
                FileName = fileName,
                MediaType = mediaType,
                FileSizeBytes = size,
            });
        }

        // Lokasi utama di event XDK: top-level attachments[]
        if (evt.TryGetProperty("attachments", out var topAtts) && topAtts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in topAtts.EnumerateArray())
                TryAddMediaObj(a);
        }

        // Fallback: content.attachments[].media
        if (list.Count == 0 &&
            evt.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("attachments", out var cAtts) && cAtts.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in cAtts.EnumerateArray())
            {
                if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("media", out var mediaEl))
                    TryAddMediaObj(mediaEl);
                else
                    TryAddMediaObj(a);
            }
        }

        return list;
    }

    /// <summary>Ekstrak JsonObject event DM plaintext (/2/dm_events) menjadi ChatMessage (teks &amp;/atau lampiran).</summary>
    public static ChatMessage? FromPlaintextDmEvent(
        JsonObject evt, IReadOnlyDictionary<string, JsonObject>? mediaByKey = null)
    {
        var eventType = evt["event_type"]?.GetValue<string>();
        if (eventType is not null && eventType != "MessageCreate")
            return null;

        var id = evt["id"]?.GetValue<string>();
        var text = evt["text"]?.GetValue<string>();
        var senderId = evt["sender_id"]?.GetValue<string>();
        var convId = evt["dm_conversation_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(senderId))
            return null;

        var attachments = ExtractPlaintextAttachments(evt, mediaByKey);
        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
            return null;

        var finalText = string.IsNullOrWhiteSpace(text)
            ? "[Attachment] " + string.Join(", ", attachments.Select(a => a.FileName ?? a.MediaType ?? "media"))
            : text;

        var createdRaw = evt["created_at"]?.GetValue<string>();
        var createdUtc = CrmUtility.TryParseXTimestamp(createdRaw, out var parsed)
            ? parsed
            : DateTime.UtcNow;

        return new ChatMessage
        {
            MessageId = id,
            ConversationId = convId ?? "",
            SenderId = senderId,
            Text = finalText,
            CreatedUtc = createdUtc,
            IsEncrypted = false,
            Attachments = attachments,
        };
    }

    private static List<ChatAttachment> ExtractPlaintextAttachments(
        JsonObject evt, IReadOnlyDictionary<string, JsonObject>? mediaByKey)
    {
        var list = new List<ChatAttachment>();
        if (mediaByKey is null || evt["attachments"]?["media_keys"] is not JsonArray keys)
            return list;

        foreach (var kNode in keys)
        {
            var key = kNode?.GetValue<string>();
            if (string.IsNullOrEmpty(key) || !mediaByKey.TryGetValue(key, out var media))
                continue;

            var type = media["type"]?.GetValue<string>();
            var url = media["url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(url) && media["variants"] is JsonArray variants)
            {
                url = variants.OfType<JsonObject>()
                    .Where(v => v["content_type"]?.GetValue<string>() == "video/mp4" && v["url"] is not null)
                    .OrderByDescending(v => v["bit_rate"]?.GetValue<long>() ?? 0)
                    .Select(v => v["url"]!.GetValue<string>())
                    .FirstOrDefault();
            }
            url ??= media["preview_image_url"]?.GetValue<string>();
            if (string.IsNullOrEmpty(url))
                continue;

            var ext = GuessExtensionFromUrlOrType(url, type);
            list.Add(new ChatAttachment
            {
                LegacyUrl = url,
                MediaType = type,
                FileName = $"{key}{ext}",
            });
        }
        return list;
    }

    internal static string GuessMimeTypeFromFileName(string? fileName, string? mediaType = null)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            _ => (mediaType ?? "").ToLowerInvariant() switch
            {
                "image" or "photo" => "image/jpeg",
                "video" or "animated_gif" => "video/mp4",
                _ => "application/octet-stream",
            },
        };
    }

    internal static string GuessExtensionFromMimeOrType(string? mime, string? mediaType)
    {
        return (mime ?? "").ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".m4a",
            "audio/ogg" => ".ogg",
            "application/pdf" => ".pdf",
            _ => (mediaType ?? "").ToLowerInvariant() switch
            {
                "image" or "photo" => ".jpg",
                "video" or "animated_gif" => ".mp4",
                _ => ".bin",
            },
        };
    }

    private static string GuessExtensionFromUrlOrType(string url, string? mediaType)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (!string.IsNullOrEmpty(ext) && ext.Length <= 6)
                return ext;
        }
        catch { /* abaikan URL non-standar */ }
        return GuessExtensionFromMimeOrType(null, mediaType);
    }

    private static string SanitizeFileName(string? rawName, string? mime, string? mediaType)
    {
        var name = Path.GetFileName(rawName ?? "").Trim();
        if (string.IsNullOrEmpty(name))
            name = "attachment" + GuessExtensionFromMimeOrType(mime, mediaType);
        else if (string.IsNullOrEmpty(Path.GetExtension(name)))
            name += GuessExtensionFromMimeOrType(mime, mediaType);
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }

    /// <summary>Conversation 1:1 berbentuk "idKecil-idBesar" (API) atau
    /// "idKecil:idBesar" (event terdekripsi XDK); ambil pasangan kita.</summary>
    private static string? InferCounterpart(string conversationId, string myUserId)
    {
        var parts = conversationId.Split('-', ':');
        if (parts.Length != 2) return null;
        return parts[0] == myUserId ? parts[1] : parts[0];
    }

    private static string Truncate(string s, int max) =>
        s.Length >= max ? s.Substring(0, max) : s;
}
