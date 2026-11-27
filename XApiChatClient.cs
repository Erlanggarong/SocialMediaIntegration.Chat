// ============================================================
// XApiChatClient — klien minimal Chat API + registrasi kunci (X API v2)
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Endpoint (spesifikasi OpenAPI resmi api.x.com/2/openapi.json):
//   GET  /2/users/me                                  -> user id authenticated (verifikasi kredensial)
//   GET/POST /2/users/{id}/public_keys                -> ambil/registrasi kunci publik (bootstrap)
//   GET  /2/chat/conversations                        -> daftar percakapan terenkripsi (X Chat)
//   GET  /2/chat/conversations/{id}/events            -> event percakapan terenkripsi (X Chat)
//   POST /2/chat/conversations/{id}/keys & /messages  -> key change & kirim pesan terenkripsi
//   GET  /2/dm_events                                 -> event DM tidak terenkripsi (plaintext klasik)
//   POST /2/dm_conversations/with/{id}/messages       -> kirim DM tidak terenkripsi (fallback plaintext)
//   GET  /2/users/{id}/mentions                       -> timeline mention akun (komentar/tag publik)
//   POST /2/tweets                                    -> kirim tweet / balasan mention publik
//
// Semua request OAuth 1.0a UserToken; pagination via meta.next_token.
// ============================================================

using System.Text.Json;
using System.Text.Json.Nodes;
using ChatXdk;

namespace SocialMediaIntegration.Chat;

public sealed class XApiChatClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly XCredentials _cred;

    public XApiChatClient(XCredentials cred) => _cred = cred;

    public void Dispose() => _http.Dispose();

    private async Task<JsonNode> GetAsync(string path, Dictionary<string, string>? query = null)
    {
        var body = await OAuth1.SendSignedAsync(
            _http, _cred, HttpMethod.Get, $"https://api.x.com{path}", query);
        return JsonNode.Parse(body)!;
    }

    private async Task<JsonNode> PostAsync(string path, JsonNode payload, bool retryOn429 = true)
    {
        var body = await OAuth1.SendSignedAsync(
            _http, _cred, HttpMethod.Post, $"https://api.x.com{path}", jsonBody: payload.ToJsonString(),
            retryOn429: retryOn429);
        return string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body)!;
    }

    /// <summary>User id (numerik) pemilik token — hasil GET /2/users/me.</summary>
    public async Task<string> GetAuthenticatedUserIdAsync()
    {
        var resp = await GetAsync("/2/users/me");
        var id = resp["data"]?["id"]?.GetValue<string>();
        if (id is null)
            throw new InvalidOperationException($"Respons /2/users/me tidak berisi data.id: {resp}");
        return id;
    }

    /// <summary>
    /// Registrasi kunci publik akun. Mengembalikan public_key_version yang
    /// dikembalikan API — wajib disimpan ke meta blob (SigningKeyVersion).
    /// </summary>
    public async Task<string> RegisterPublicKeyAsync(string userId, KeyPairPayload pub)
    {
        // Catatan: public_key_fingerprint SENGAJA tidak dikirim. Contoh resmi
        // (examples/dotnet/ChatBot/Register.cs) tidak mengirimnya, dan server
        // menolak request yang memuat field itu ("invalid request parameters").
        var pk = new JsonObject
        {
            ["public_key"] = pub.Identity,
            ["signing_public_key"] = pub.Signing,
            ["identity_public_key_signature"] = pub.IdentityPublicKeySignature,
            ["registration_method"] = pub.RegistrationMethod,
        };
        if (pub.SigningPublicKeySignature is not null)
            pk["signing_public_key_signature"] = pub.SigningPublicKeySignature;

        var request = new JsonObject { ["public_key"] = pk };
        // retryOn429: false — endpoint ini dibatasi 5 request/24 jam per user
        // (lihat portal Rate limits); retry otomatis hanya memboroskan kuota.
        var resp = await PostAsync($"/2/users/{userId}/public_keys", request, retryOn429: false);
        var version = resp["data"]?["public_key_version"]?.GetValue<string>();
        if (version is null)
            throw new InvalidOperationException($"Respons registrasi tidak berisi public_key_version: {resp}");
        return version;
    }

    /// <summary>
    /// Daftar public key yang sudah terdaftar di akun (GET /2/users/{id}/public_keys).
    /// Dipakai untuk reconcile: cek apakah key kita sudah terdaftar SEBELUM POST,
    /// karena POST berkuota 5 request/24 jam — POST duplikat membuang kuota.
    /// </summary>
    public async Task<JsonArray> GetPublicKeysAsync(string userId)
    {
        var resp = await GetAsync($"/2/users/{userId}/public_keys");
        return resp["data"] as JsonArray ?? new JsonArray();
    }

    /// <summary>
    /// Ambil respons penuh GET /2/users/{id}/public_keys dengan permintaan
    /// field juicebox_config (untuk inspeksi &amp; pemulihan kunci aplikasi resmi via PIN).
    /// </summary>
    public async Task<JsonNode> GetPublicKeysFullAsync(string userId, bool requestJuiceboxFields = true)
    {
        Dictionary<string, string>? query = requestJuiceboxFields
            ? new Dictionary<string, string>
            {
                ["public_key.fields"] = "public_key,signing_public_key,identity_public_key_signature,public_key_version,juicebox_config",
            }
            : null;
        return await GetAsync($"/2/users/{userId}/public_keys", query);
    }

    /// <summary>
    /// Rotasi/pembuatan conversation key: POST /2/chat/conversations/{id}/keys.
    /// Dipakai saat memulai percakapan 1:1 via API atau rotasi key agar key
    /// kita (identity terdaftar) ikut dibungkus — tanpa ini pesan lama yang
    /// key change-nya dibuat sebelum registrasi tidak akan bisa kita baca.
    /// </summary>
    public async Task AddConversationKeysAsync(string conversationIdHyphen, PreparedConversationChange change)
    {
        var request = new JsonObject
        {
            ["conversation_key_version"] = change.ConversationKeyVersion,
            ["conversation_participant_keys"] = JsonSerializer.SerializeToNode(change.ConversationParticipantKeys),
            ["action_signatures"] = JsonSerializer.SerializeToNode(change.ActionSignatures),
        };
        await PostAsync($"/2/chat/conversations/{conversationIdHyphen}/keys", request);
    }

    /// <summary>Kirim pesan terenkripsi: POST /2/chat/conversations/{id}/messages.</summary>
    public async Task SendChatMessageAsync(string conversationIdHyphen, SendPayload payload)
    {
        var request = new JsonObject
        {
            ["message_id"] = payload.MessageId,
            ["encoded_message_create_event"] = payload.EncodedMessageCreateEvent,
            ["encoded_message_event_signature"] = payload.EncodedMessageEventSignature,
            ["xdk_version"] = payload.XdkVersion,
        };
        await PostAsync($"/2/chat/conversations/{conversationIdHyphen}/messages", request);
    }

    /// <summary>
    /// Kirim DM tidak terenkripsi (plaintext klasik): POST /2/dm_conversations/with/{participantId}/messages.
    /// Dipakai sebagai fallback bila penerima tidak memiliki public key X Chat terdaftar.
    /// Mengembalikan dm_event_id dari respons X API.
    /// </summary>
    public async Task<string> SendPlaintextDmAsync(
        string participantId, string text, IReadOnlyList<string>? mediaIds = null)
    {
        var request = new JsonObject { ["text"] = text };
        if (mediaIds is { Count: > 0 })
        {
            var attArr = new JsonArray();
            foreach (var mid in mediaIds)
                attArr.Add(new JsonObject { ["media_id"] = mid });
            request["attachments"] = attArr;
        }
        var resp = await PostAsync($"/2/dm_conversations/with/{participantId}/messages", request);
        var eventId = resp["data"]?["dm_event_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(eventId))
            throw new InvalidOperationException($"Respons kirim DM plaintext tidak berisi data.dm_event_id: {resp}");
        return eventId;
    }

    /// <summary>
    /// Inisialisasi sesi upload media terenkripsi X Chat: POST /2/chat/media/upload/initialize.
    /// Catatan (sesuai contoh resmi chat-xdk XChatClient.cs): endpoint upload media memakai
    /// conversation_id berformat COLON ("min:max"), sedangkan download memakai HYPHEN ("min-max").
    /// </summary>
    public async Task<(string SessionId, string MediaHashKey, string ConversationId)> InitializeChatMediaUploadAsync(
        string conversationId, long totalEncryptedBytes)
    {
        var colonConvId = conversationId.Replace('-', ':');
        var payload = new JsonObject
        {
            ["conversation_id"] = colonConvId,
            ["total_bytes"] = totalEncryptedBytes,
        };
        var resp = await PostAsync("/2/chat/media/upload/initialize", payload);
        var data = resp["data"] as JsonObject ?? resp as JsonObject;
        var sessionId = data?["session_id"]?.GetValue<string>()
                        ?? data?["id"]?.GetValue<string>()
                        ?? data?["media_id"]?.GetValue<string>();
        var mediaHashKey = data?["media_hash_key"]?.GetValue<string>();
        var canonicalConvId = (data?["conversation_id"]?.GetValue<string>() ?? colonConvId).Replace('-', ':');
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(mediaHashKey))
            throw new InvalidOperationException(
                $"Respons /2/chat/media/upload/initialize tidak berisi session_id & media_hash_key: {resp}");
        return (sessionId, mediaHashKey, canonicalConvId);
    }

    /// <summary>
    /// Unggah ciphertext media X Chat per chunk 3 MB (base64 JSON) lalu finalize:
    /// POST /2/chat/media/upload/{sessionId}/append -> finalize.
    /// Mengikuti contoh resmi chat-xdk (XChatClient.cs): conversation_id memakai format colon
    /// dan segment_index / num_parts dikirim sebagai string numerik.
    /// </summary>
    public async Task UploadEncryptedChatMediaChunksAsync(
        string conversationId, string sessionId, string mediaHashKey, byte[] encryptedBytes)
    {
        const int chunkSize = 3 * 1024 * 1024;
        var colonConvId = conversationId.Replace('-', ':');
        var segment = 0;

        for (var offset = 0; offset < encryptedBytes.Length; offset += chunkSize)
        {
            var len = Math.Min(chunkSize, encryptedBytes.Length - offset);
            var appendPayload = new JsonObject
            {
                ["conversation_id"] = colonConvId,
                ["media_hash_key"] = mediaHashKey,
                ["segment_index"] = segment.ToString(),
                ["media"] = Convert.ToBase64String(encryptedBytes, offset, len),
            };
            await PostAsync($"/2/chat/media/upload/{Uri.EscapeDataString(sessionId)}/append", appendPayload);
            segment++;
        }

        var finalizePayload = new JsonObject
        {
            ["conversation_id"] = colonConvId,
            ["media_hash_key"] = mediaHashKey,
            ["num_parts"] = segment.ToString(),
        };
        await PostAsync($"/2/chat/media/upload/{Uri.EscapeDataString(sessionId)}/finalize", finalizePayload);
    }

    /// <summary>
    /// Unggah media untuk DM plaintext (X API v2 chunked upload, category dm_*). Mengembalikan media_id.
    /// </summary>
    public Task<string> UploadPlaintextDmMediaAsync(byte[] fileBytes, string mimeType, string fileName) =>
        UploadMediaV2Async(fileBytes, mimeType, fileName, categoryPrefix: "dm");

    /// <summary>
    /// Unggah media untuk tweet / balasan mention publik (X API v2 chunked upload, category tweet_*).
    /// </summary>
    public Task<string> UploadTweetMediaAsync(byte[] fileBytes, string mimeType, string fileName) =>
        UploadMediaV2Async(fileBytes, mimeType, fileName, categoryPrefix: "tweet");

    private async Task<string> UploadMediaV2Async(
        byte[] fileBytes, string mimeType, string fileName, string categoryPrefix)
    {
        var category = mimeType.ToLowerInvariant() switch
        {
            "image/gif" => $"{categoryPrefix}_gif",
            var m when m.StartsWith("video/", StringComparison.Ordinal) => $"{categoryPrefix}_video",
            _ => $"{categoryPrefix}_image",
        };
        var initResp = await PostAsync("/2/media/upload/initialize", new JsonObject
        {
            ["total_bytes"] = fileBytes.Length,
            ["media_type"] = mimeType,
            ["media_category"] = category,
        });
        var mediaId = initResp["data"]?["id"]?.GetValue<string>()
                      ?? throw new InvalidOperationException($"Respons /2/media/upload/initialize tanpa data.id: {initResp}");

        const int chunkSize = 4 * 1024 * 1024;
        var totalParts = Math.Max(1, (fileBytes.Length + chunkSize - 1) / chunkSize);
        for (var part = 0; part < totalParts; part++)
        {
            var offset = part * chunkSize;
            var len = Math.Min(chunkSize, fileBytes.Length - offset);
            var segIndex = part;
            await OAuth1.SendSignedMultipartAsync(
                _http, _cred, $"https://api.x.com/2/media/upload/{mediaId}/append",
                () =>
                {
                    var mp = new MultipartFormDataContent();
                    mp.Add(new StringContent(segIndex.ToString()), "segment_index");
                    var bin = new ByteArrayContent(fileBytes, offset, len);
                    bin.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
                    mp.Add(bin, "media", fileName);
                    return mp;
                });
        }

        await PostAsync($"/2/media/upload/{mediaId}/finalize", new JsonObject());
        return mediaId;
    }

    /// <summary>
    /// Kirim tweet atau balasan mention publik: POST /2/tweets.
    /// Jika inReplyToTweetId diisi, otomatis menjadi reply ke tweet tersebut.
    /// Mengembalikan tweet id baru dari respons X API.
    /// </summary>
    public async Task<string> PostTweetAsync(
        string text, string? inReplyToTweetId = null, IReadOnlyList<string>? mediaIds = null)
    {
        var payload = new JsonObject { ["text"] = text };
        if (!string.IsNullOrWhiteSpace(inReplyToTweetId))
        {
            payload["reply"] = new JsonObject
            {
                ["in_reply_to_tweet_id"] = inReplyToTweetId.Trim(),
            };
        }
        if (mediaIds is { Count: > 0 })
        {
            var idsArr = new JsonArray();
            foreach (var mid in mediaIds)
                idsArr.Add(mid);
            payload["media"] = new JsonObject
            {
                ["media_ids"] = idsArr,
            };
        }

        var resp = await PostAsync("/2/tweets", payload);
        var tweetId = resp["data"]?["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(tweetId))
            throw new InvalidOperationException($"Respons POST /2/tweets tidak berisi data.id: {resp}");
        return tweetId;
    }

    /// <summary>
    /// Download ciphertext lampiran terenkripsi X Chat: GET /2/chat/media/{conversationId}/{mediaHashKey}.
    /// Hasil berupa byte terenkripsi yang wajib didekripsi via Chat.DecryptFile.
    /// </summary>
    public Task<byte[]> DownloadChatMediaAsync(string conversationId, string mediaHashKey)
    {
        var hyphenId = conversationId.Replace(':', '-');
        var url = $"https://api.x.com/2/chat/media/{hyphenId}/{Uri.EscapeDataString(mediaHashKey)}";
        return OAuth1.SendSignedBytesAsync(_http, _cred, HttpMethod.Get, url);
    }

    /// <summary>Download media dari URL X (untuk lampiran DM plaintext/legacy) dengan OAuth 1.0a.</summary>
    public Task<byte[]> DownloadUrlBytesAsync(string url) =>
        OAuth1.SendSignedBytesAsync(_http, _cred, HttpMethod.Get, url);

    /// <summary>
    /// Tarik event DM tidak terenkripsi (plaintext klasik): GET /2/dm_events,
    /// termasuk ekspansi media lampiran (includes.media).
    /// maxPages &gt; 0 membatasi jumlah halaman pagination (default 5 = maks 500 event terbaru);
    /// &lt;= 0 berarti tarik semua halaman.
    /// Bila stopBeforeUtc diisi, pagination berhenti otomatis begitu menemui event lebih lama
    /// dari batas tersebut (urutan X API: terbaru -> terlama).
    /// </summary>
    public async Task<(IReadOnlyList<JsonObject> Events, IReadOnlyDictionary<string, JsonObject> MediaByKey)>
        GetPlaintextDmEventsAsync(int maxPages = 5, DateTime? stopBeforeUtc = null)
    {
        var result = new List<JsonObject>();
        var mediaByKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        string? nextToken = null;
        var page = 0;
        var reachedCutoff = false;
        do
        {
            var query = new Dictionary<string, string>
            {
                ["event_types"] = "MessageCreate",
                ["dm_event.fields"] = "id,text,event_type,created_at,dm_conversation_id,sender_id,attachments",
                ["expansions"] = "attachments.media_keys",
                ["media.fields"] = "url,preview_image_url,type,variants",
                ["max_results"] = "100",
            };
            if (nextToken is not null) query["pagination_token"] = nextToken;
            var resp = await GetAsync("/2/dm_events", query);

            if (resp["includes"]?["media"] is JsonArray mediaArr)
            {
                foreach (var m in mediaArr.OfType<JsonObject>())
                {
                    var key = m["media_key"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(key))
                        mediaByKey[key] = m;
                }
            }

            if (resp["data"] is JsonArray arr)
            {
                foreach (var e in arr)
                {
                    if (e is not JsonObject evt) continue;
                    if (stopBeforeUtc is { } cutoff &&
                        CrmUtility.TryParseXTimestamp(evt["created_at"]?.GetValue<string>(), out var createdUtc) &&
                        createdUtc < cutoff)
                    {
                        reachedCutoff = true;
                        break;
                    }
                    result.Add(evt);
                }
            }
            nextToken = resp["meta"]?["next_token"]?.GetValue<string>();
            page++;
        } while (!reachedCutoff && nextToken is not null && (maxPages <= 0 || page < maxPages));
        return (result, mediaByKey);
    }

    /// <summary>
    /// Tarik timeline mention akun X: GET /2/users/{userId}/mentions.
    /// Mendukung filter server-side start_time (ISO 8601 UTC) serta ekspansi author &amp; media.
    /// </summary>
    public async Task<(IReadOnlyList<JsonObject> Tweets, IReadOnlyDictionary<string, JsonObject> UsersById, IReadOnlyDictionary<string, JsonObject> MediaByKey)>
        GetMentionsAsync(string userId, int maxPages = 5, DateTime? startTimeUtc = null)
    {
        var tweets = new List<JsonObject>();
        var usersById = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var mediaByKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        string? nextToken = null;
        var page = 0;

        do
        {
            var query = new Dictionary<string, string>
            {
                ["max_results"] = "100",
                ["expansions"] = "author_id,attachments.media_keys",
                ["tweet.fields"] = "created_at,attachments,referenced_tweets",
                ["user.fields"] = "name,username",
                ["media.fields"] = "url,preview_image_url,type",
            };
            if (startTimeUtc is { } start)
                query["start_time"] = start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            if (nextToken is not null)
                query["pagination_token"] = nextToken;

            var resp = await GetAsync($"/2/users/{userId}/mentions", query);

            if (resp["includes"]?["users"] is JsonArray usersArr)
            {
                foreach (var u in usersArr.OfType<JsonObject>())
                {
                    var id = u["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id))
                        usersById[id] = u;
                }
            }
            if (resp["includes"]?["media"] is JsonArray mediaArr)
            {
                foreach (var m in mediaArr.OfType<JsonObject>())
                {
                    var key = m["media_key"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(key))
                        mediaByKey[key] = m;
                }
            }
            if (resp["data"] is JsonArray dataArr)
            {
                foreach (var t in dataArr.OfType<JsonObject>())
                    tweets.Add(t);
            }

            nextToken = resp["meta"]?["next_token"]?.GetValue<string>();
            page++;
        } while (nextToken is not null && (maxPages <= 0 || page < maxPages));

        return (tweets, usersById, mediaByKey);
    }

    /// <summary>Profil user (id -> (name, username)) untuk satu atau lebih id numerik (batch max 100/request sesuai batas X API v2).</summary>
    public async Task<Dictionary<string, (string Name, string Username)>> GetUsersByIdsAsync(
        IEnumerable<string> userIds)
    {
        var result = new Dictionary<string, (string, string)>();
        var distinctIds = userIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal);

        foreach (var batch in distinctIds.Chunk(100))
        {
            var ids = string.Join(",", batch);
            var resp = await GetAsync("/2/users", new Dictionary<string, string>
            {
                ["ids"] = ids,
                ["user.fields"] = "name,username",
            });
            if (resp["data"] is not JsonArray arr) continue;
            foreach (var u in arr)
            {
                if (u is not JsonObject user) continue;
                var id = user["id"]?.GetValue<string>();
                if (id is null) continue;
                result[id] = (user["name"]?.GetValue<string>() ?? id, user["username"]?.GetValue<string>() ?? id);
            }
        }
        return result;
    }

    /// <summary>Semua percakapan (jalan semua halaman pagination).</summary>
    public async Task<IReadOnlyList<JsonObject>> GetAllConversationsAsync()
    {
        var result = new List<JsonObject>();
        string? nextToken = null;
        do
        {
            var query = new Dictionary<string, string> { ["max_results"] = "100" };
            if (nextToken is not null) query["pagination_token"] = nextToken;
            var resp = await GetAsync("/2/chat/conversations", query);
            if (resp["data"] is JsonArray arr)
                foreach (var c in arr)
                    if (c is JsonObject conv) result.Add(conv);
            nextToken = resp["meta"]?["next_token"]?.GetValue<string>();
        } while (nextToken is not null);
        return result;
    }

    /// <summary>
    /// Semua event satu percakapan (jalan semua halaman).
    /// conversationKeyEvents diisi dari meta.conversation_key_events —
    /// wajib ikut diberikan ke DecryptEvents (lihat MIGRASI-CHAT-XDK.md Fase 3).
    ///
    /// PENTING: event di data[] membungkus payload terenkripsi di field
    /// "encoded_event" (bukan "event") — lihat ChatMessageEvent di OpenAPI.
    /// </summary>
    public async Task<(IReadOnlyList<string> Events, IReadOnlyList<string> KeyEvents)> GetAllEventsAsync(
        string conversationId)
    {
        var events = new List<string>();
        var keyEvents = new List<string>();
        string? nextToken = null;
        do
        {
            var query = new Dictionary<string, string> { ["max_results"] = "100" };
            if (nextToken is not null) query["pagination_token"] = nextToken;
            var resp = await GetAsync($"/2/chat/conversations/{conversationId}/events", query);
            if (resp["data"] is JsonArray arr)
                foreach (var e in arr)
                    // ChatMessageEvent: payload terenkripsi base64 di "encoded_event".
                    if (e?["encoded_event"]?.GetValue<string>() is string b64)
                        events.Add(b64);
            if (resp["meta"]?["conversation_key_events"] is JsonArray ke)
                foreach (var e in ke)
                    if (e?.GetValue<string>() is string b64)
                        keyEvents.Add(b64);
            nextToken = resp["meta"]?["next_token"]?.GetValue<string>();
        } while (nextToken is not null);
        return (events, keyEvents);
    }
}

/// <summary>Payload kunci publik hasil GenerateKeypairs (bagian yang dipakai untuk registrasi).</summary>
public sealed class KeyPairPayload
{
    public required string Identity { get; init; }
    public required string Signing { get; init; }
    public required string IdentityPublicKeySignature { get; init; }
    public string? SigningPublicKeySignature { get; init; }
    public required string? PublicKeyFingerprint { get; init; }
    public required string RegistrationMethod { get; init; }
}
