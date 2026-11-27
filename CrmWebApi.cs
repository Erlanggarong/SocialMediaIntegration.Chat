// ============================================================
// CrmWebApi — koneksi CRM on-premise (Dynamics 365/CRM 9.1) via Web API
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Kenapa tidak pakai Microsoft.PowerPlatform.Dataverse.Client:
//   library itu tidak mendukung autentikasi AD on-premise di .NET 8
//   ("ServiceModel metadata support is limited for this target framework").
//   WCF OrganizationServiceProxy (dipakai console .NET 4.5.2 lama) hanya ada
//   di .NET Framework, sedangkan SDK ChatXdk hanya tersedia untuk net8.0.
//
// Solusi: Web API CRM (/api/data/v9.0) dengan NTLM dari HttpClient —
// tanpa WCF, tanpa SDK, bekerja di .NET 8 dan di server on-prem AD.
//
// Connection string (diparse manual, format menyerupai pola SDK):
//   AuthType=AD;Url=http://server:port/ORG;Domain=DOMAIN;Username=user;Password=pass
// (AuthType diabaikan — selalu NTLM; kredensial tidak pernah disimpan ke disk
// oleh program ini, hanya dibaca dari env var CRM_CONNECTION_STRING.)
// ============================================================

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SocialMediaIntegration.Chat;

public sealed class CrmWebApi : IDisposable
{
    private readonly HttpClient _http;

    public string ApiBase { get; }

    public CrmWebApi(string connectionString)
    {
        var kv = ParseConnectionString(connectionString);
        if (!kv.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("CRM_CONNECTION_STRING wajib memuat Url=...", nameof(connectionString));
        if (!kv.TryGetValue("username", out var user) || string.IsNullOrWhiteSpace(user))
            throw new ArgumentException("CRM_CONNECTION_STRING wajib memuat Username=...", nameof(connectionString));
        kv.TryGetValue("password", out var pass);
        kv.TryGetValue("domain", out var domain);

        ApiBase = url.TrimEnd('/') + "/api/data/v9.0/";
        if (!Uri.TryCreate(ApiBase, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(
                $"Url CRM tidak valid (harus absolute http/https): '{ApiBase}' — periksa env var CRM_CONNECTION_STRING.",
                nameof(connectionString));
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(user, pass ?? "", domain ?? ""),
            PreAuthenticate = true,
        };
        _http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static Dictionary<string, string> ParseConnectionString(string cs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in cs.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            // Bersihkan kutip liar/spasi — setx PowerShell/cmd kadang menyimpan
            // tanda kutip pembungkus ke dalam nilai env var.
            var key = part[..eq].Trim().Trim('"').Trim();
            var val = part[(eq + 1)..].Trim().Trim('"').Trim();
            if (key.Length > 0)
                result[key] = val;
        }
        return result;
    }

    /// <summary>Escape literal string untuk OData $filter.</summary>
    public static string Esc(string s) => s.Replace("'", "''");

    public async Task<JsonDocument> GetAsync(string path)
    {
        var resp = await _http.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"CRM GET gagal ({(int)resp.StatusCode}): {Truncate(text, 300)}");
        return JsonDocument.Parse(text);
    }

    /// <summary>Ambil record pertama yang cocok filter; null bila tidak ada.</summary>
    public async Task<JsonElement?> RetrieveFirstAsync(string entity, string filter, string? select = null, string? orderby = null)
    {
        var qs = $"$filter={Uri.EscapeDataString(filter)}&$top=1";
        if (select is not null) qs += $"&$select={Uri.EscapeDataString(select)}";
        if (orderby is not null) qs += $"&$orderby={Uri.EscapeDataString(orderby)}";
        using var doc = await GetAsync($"{entity}?{qs}");
        var arr = doc.RootElement.GetProperty("value");
        return arr.GetArrayLength() > 0 ? arr[0].Clone() : null;
    }

    /// <summary>Ambil SEMUA record yang cocok filter (mengikuti paging @odata.nextLink).</summary>
    public async Task<List<JsonElement>> RetrieveMultipleAsync(string entity, string filter, string? select = null)
    {
        var qs = $"$filter={Uri.EscapeDataString(filter)}";
        if (select is not null) qs += $"&$select={Uri.EscapeDataString(select)}";

        var list = new List<JsonElement>();
        string? nextPath = $"{entity}?{qs}";
        while (!string.IsNullOrWhiteSpace(nextPath))
        {
            using var doc = await GetAsync(nextPath);
            var arr = doc.RootElement.GetProperty("value");
            foreach (var el in arr.EnumerateArray())
                list.Add(el.Clone());

            nextPath = doc.RootElement.TryGetProperty("@odata.nextLink", out var nextEl) &&
                       nextEl.ValueKind == JsonValueKind.String
                ? nextEl.GetString()
                : null;
        }
        return list;
    }

    /// <summary>Update (PATCH) record berdasarkan GUID id.</summary>
    public async Task UpdateAsync(string entity, string id, JsonObject body)
    {
        var payload = body.ToJsonString();
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.PatchAsync($"{entity}({id})", content);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"CRM update {entity}({id}) gagal ({(int)resp.StatusCode}): {Truncate(text, 500)}\n  payload: {Truncate(payload, 500)}");
    }

    /// <summary>Buat record; mengembalikan GUID record baru (dari header OData-EntityId).</summary>
    public async Task<string> CreateAsync(string entity, JsonObject body)
    {
        var payload = body.ToJsonString();
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(entity, content);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"CRM create {entity} gagal ({(int)resp.StatusCode}): {Truncate(text, 1000)}\n  payload: {Truncate(payload, 1000)}");

        if (resp.Headers.TryGetValues("OData-EntityId", out var vals))
        {
            var loc = vals.First();
            var open = loc.LastIndexOf('(');
            var close = loc.LastIndexOf(')');
            if (open >= 0 && close > open)
                return loc[(open + 1)..close].Trim('\'');
        }
        // Fallback: baca id dari body respons.
        if (!string.IsNullOrWhiteSpace(text))
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("id", out var idEl))
                return idEl.GetString() ?? throw new InvalidOperationException("CRM create: id respons kosong.");
        }
        throw new InvalidOperationException("CRM create: OData-EntityId tidak ditemukan pada respons.");
    }

    /// <summary>GET mentah (non-JSON), untuk endpoint seperti $metadata (CSDL/XML).</summary>
    private async Task<string> GetRawAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/xml"));
        var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"CRM GET gagal ({(int)resp.StatusCode}): {Truncate(text, 300)}");
        return text;
    }

    private readonly Dictionary<(string Entity, string Target), string?> _navCache = new();

    /// <summary>
    /// Cari nama navigation property Web API untuk lookup entity -> target.
    /// Di-cache per proses. Sumber kebenaran: dokumen $metadata (CSDL) —
    /// cari EntityType 'entity', NavigationProperty ber-tipe 'mscrm.targetEntity'
    /// (bukan Collection). Jangan pakai SchemaName relasi ManyToOne: itu nama
    /// relasi 1:N di sisi target, BUKAN nama navigation property sisi N:1
    /// (contoh nyata: relasi 'bi_bi_precontact_bi_twitter_Precontact', nav-nya
    /// 'bi_Precontact_bi_twitter').
    /// </summary>
    public async Task<string?> GetLookupNavigationNameAsync(string entity, string targetEntity)
    {
        var key = (entity, targetEntity);
        if (_navCache.TryGetValue(key, out var cached))
            return cached;
        string? name = null;
        var csdl = await GetRawAsync("$metadata");
        var xdoc = System.Xml.Linq.XDocument.Parse(csdl);
        // Cocokkan pakai LocalName: root $metadata adalah <edmx:Edmx> (prefixed)
        // dan default namespace edm baru dideklarasikan di <Schema> dalam, sehingga
        // GetDefaultNamespace() dari root mengembalikan namespace kosong.
        var et = xdoc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "EntityType" &&
                string.Equals(e.Attribute("Name")?.Value, entity, StringComparison.OrdinalIgnoreCase));
        if (et is not null)
        {
            var wantedType = $"mscrm.{targetEntity}";
            name = et.Elements()
                .Where(p => p.Name.LocalName == "NavigationProperty")
                .FirstOrDefault(p => string.Equals(
                    p.Attribute("Type")?.Value, wantedType, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("Name")?.Value;
        }
        _navCache[key] = name;
        return name;
    }

    /// <summary>Uji koneksi: WhoAmI. Melempar exception bila gagal.</summary>
    public async Task<string> PingAsync()
    {
        using var doc = await GetAsync("WhoAmI");
        return doc.RootElement.GetProperty("UserId").GetString() ?? "?";
    }

    /// <summary>
    /// Cari precontact berdasarkan user id X (bi_source = 3); buat bila belum ada.
    /// Port identik CrmUtility.GetPrecontact (console .NET 4.5.2 lama).
    /// </summary>
    public async Task<(string PrecontactId, string? ContactId)> GetOrCreatePrecontactAsync(
        string screenName, string userId, string displayName)
    {
        var filter = $"bi_id eq '{Esc(userId)}' and bi_source eq 3";
        var found = await RetrieveFirstAsync("bi_precontacts", filter, "_bi_contact_value,bi_precontactid");
        if (found is { } el)
        {
            var id = el.TryGetProperty("bi_precontactid", out var pid) ? pid.GetString() : null;
            var contact = el.TryGetProperty("_bi_contact_value", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
            if (id is not null)
                return (id, contact);
        }

        var newId = await CreateAsync("bi_precontacts", new JsonObject
        {
            ["bi_name"] = displayName,
            ["bi_id"] = userId,
            ["bi_screenname"] = screenName,
            ["bi_source"] = 3,
        });
        return (newId, null);
    }

    /// <summary>
    /// Baca record konfigurasi aktif (statecode = 0) dari entity new_configuration
    /// berdasarkan new_name (mis. "X Last Sync").
    /// </summary>
    public async Task<(string? Id, string? Value)> GetConfigurationAsync(string name)
    {
        var filter = $"new_name eq '{Esc(name)}' and statecode eq 0";
        var found = await RetrieveFirstAsync("new_configurations", filter, "new_configurationid,new_value");
        if (found is not { } el)
            return (null, null);

        var id = el.TryGetProperty("new_configurationid", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() : null;
        var val = el.TryGetProperty("new_value", out var valEl) && valEl.ValueKind == JsonValueKind.String
            ? valEl.GetString() : null;
        return (id, val);
    }

    /// <summary>
    /// Simpan nilai konfigurasi ke entity new_configuration (update bila sudah ada,
    /// buat record baru bila belum ada).
    /// </summary>
    public async Task<string> UpsertConfigurationAsync(string name, string value, string? existingId = null)
    {
        var id = existingId;
        if (string.IsNullOrEmpty(id))
            (id, _) = await GetConfigurationAsync(name);

        if (!string.IsNullOrEmpty(id))
        {
            await UpdateAsync("new_configurations", id, new JsonObject
            {
                ["new_value"] = value,
            });
            return id;
        }

        return await CreateAsync("new_configurations", new JsonObject
        {
            ["new_name"] = name,
            ["new_value"] = value,
        });
    }

    /// <summary>
    /// Unggah file lampiran sebagai Note/Attachment (entity annotation) yang terhubung
    /// ke record induk (mis. bi_twitter). Nama navigation property lookup diresolusi
    /// dinamis dari $metadata dengan fallback "objectid_{parentEntityLogicalName}".
    /// </summary>
    public async Task<string> CreateAnnotationAsync(
        string parentEntityLogicalName, string parentEntitySetName, string parentId,
        string fileName, string mimeType, byte[] data, string? subject = null)
    {
        var nav = await GetLookupNavigationNameAsync("annotation", parentEntityLogicalName)
                  ?? $"objectid_{parentEntityLogicalName}";

        var body = new JsonObject
        {
            ["subject"] = subject ?? $"Attachment - {fileName}",
            ["filename"] = fileName,
            ["mimetype"] = mimeType,
            ["documentbody"] = Convert.ToBase64String(data),
            ["isdocument"] = true,
            [$"{nav}@odata.bind"] = $"/{parentEntitySetName}({parentId})",
        };
        return await CreateAsync("annotations", body);
    }

    /// <summary>
    /// Ambil semua lampiran file (Notes dengan isdocument = true) yang menempel
    /// pada satu record induk berdasarkan GUID-nya (mis. activityid bi_twitter).
    /// </summary>
    public async Task<List<(string FileName, string MimeType, byte[] Bytes)>> GetAnnotationsForRecordAsync(string parentId)
    {
        var filter = $"_objectid_value eq {parentId} and isdocument eq true";
        var records = await RetrieveMultipleAsync(
            "annotations", filter, "annotationid,filename,mimetype,documentbody");

        var list = new List<(string, string, byte[])>();
        foreach (var rec in records)
        {
            var bodyB64 = rec.TryGetProperty("documentbody", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() : null;
            if (string.IsNullOrWhiteSpace(bodyB64))
                continue;

            var fileName = rec.TryGetProperty("filename", out var fn) && fn.ValueKind == JsonValueKind.String
                ? fn.GetString() ?? "attachment.bin" : "attachment.bin";
            var mime = rec.TryGetProperty("mimetype", out var mt) && mt.ValueKind == JsonValueKind.String
                ? mt.GetString() ?? "" : "";
            try
            {
                var bytes = Convert.FromBase64String(bodyB64);
                if (bytes.Length > 0)
                    list.Add((fileName, mime, bytes));
            }
            catch
            {
                // Abaikan documentbody yang tidak valid base64.
            }
        }
        return list;
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    public void Dispose() => _http.Dispose();
}
