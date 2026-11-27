// ============================================================
// TriggerServerCommand — Internal HTTP Trigger Listener (mode 'serve')
// SocialMediaIntegration.Chat / Bank Indonesia (BICARA)
//
// Tujuan:
//   Menjembatani Plugin Dynamics CRM 9.0 (.NET Framework 4.6.2) agar dapat
//   men-trigger pengiriman DM (E2EE/plaintext) & balasan Mention secara
//   REAL-TIME tanpa memuat assembly .NET 8 di dalam proses CRM.
//
// Endpoint (default http://127.0.0.1:5080/):
//   GET  /health                       -> status service & panjang antrean
//   POST /trigger/reply/{activityId}   -> kirim 1 record bi_twitter (auto DM / Mention)
//   POST /trigger/dm                   -> proses seluruh antrian DM (send-replies)
//   POST /trigger/mention              -> proses seluruh antrian Mention (send-mention-replies)
//   POST /trigger/sync                 -> tarik DM masuk (sync)
//   POST /trigger/sync-mentions        -> tarik Mention masuk (sync-mentions)
//
// Keamanan:
//   Set env var TRIGGER_API_KEY; setiap request POST wajib membawa header:
//     X-Api-Key: <nilai TRIGGER_API_KEY>
//   Setiap trigger langsung membalas 202 Accepted (< 50ms) dan diproses serial
//   di background worker (Channel<T>) agar plugin CRM tidak pernah timeout dan
//   tidak terjadi pengiriman ganda.
// ============================================================

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SocialMediaIntegration.Chat;

internal static class TriggerServerCommand
{
    private readonly record struct TriggerJob(string Kind, string? ActivityId);

    public static async Task<int> RunAsync(string[] args)
    {
        var verbose = args.Contains("--verbose");
        var prefix = ParsePrefix(args);
        var apiKey = Environment.GetEnvironmentVariable("TRIGGER_API_KEY")?.Trim();
        var connStr = Environment.GetEnvironmentVariable("CRM_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            Console.Error.WriteLine("CRM_CONNECTION_STRING wajib diisi untuk menjalankan mode 'serve'.");
            return 1;
        }

        // Validasi koneksi CRM saat startup agar error konfigurasi langsung ketahuan.
        using (var probeCrm = new CrmWebApi(connStr))
        {
            var who = await probeCrm.PingAsync();
            Console.WriteLine($"CRM OK ({probeCrm.ApiBase}, WhoAmI {who})");
        }

        if (string.IsNullOrEmpty(apiKey))
            Console.WriteLine("[peringatan] TRIGGER_API_KEY tidak diset — endpoint POST berjalan tanpa autentikasi header (hanya aman di localhost dev).");

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"Gagal memulai HttpListener pada '{prefix}': {ex.Message}");
            Console.Error.WriteLine("Tips: untuk bind lintas server (http://+:5080/), jalankan sekali sebagai Administrator:");
            Console.Error.WriteLine($"  netsh http add urlacl url={prefix} user=\"BICARA\\SVC-XChat\"");
            return 1;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var channel = Channel.CreateBounded<TriggerJob>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        var inFlight = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nMenerima sinyal berhenti — menutup listener & menyelesaikan antrean...");
            cts.Cancel();
            try { listener.Stop(); } catch { }
        };

        Console.WriteLine($"=== serve aktif di {prefix} ===");
        Console.WriteLine("Endpoint:");
        Console.WriteLine("  GET  /health");
        Console.WriteLine("  POST /trigger/reply/{activityId}  (auto DM / Mention)");
        Console.WriteLine("  POST /trigger/dm | /trigger/mention | /trigger/sync | /trigger/sync-mentions");
        Console.WriteLine("Tekan Ctrl+C untuk berhenti.\n");

        var workerTask = RunWorkerAsync(channel.Reader, inFlight, connStr, verbose, cts.Token);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (HttpListenerException) when (cts.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }

                _ = HandleRequestAsync(ctx, apiKey, channel.Writer, inFlight, startedAt);
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            await workerTask;
        }

        Console.WriteLine("=== serve berhenti dengan bersih ===");
        return 0;
    }

    private static async Task HandleRequestAsync(
        HttpListenerContext ctx,
        string? expectedApiKey,
        ChannelWriter<TriggerJob> writer,
        ConcurrentDictionary<string, byte> inFlight,
        DateTimeOffset startedAt)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            var path = (req.Url?.AbsolutePath ?? "/").TrimEnd('/');
            if (string.IsNullOrEmpty(path)) path = "/";

            if (req.HttpMethod == "GET" && path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(res, 200, new
                {
                    status = "ok",
                    in_flight = inFlight.Count,
                    uptime_sec = (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                });
                return;
            }

            if (req.HttpMethod != "POST")
            {
                await WriteJsonAsync(res, 405, new { error = "Method not allowed. Gunakan GET /health atau POST /trigger/..." });
                return;
            }

            if (!string.IsNullOrEmpty(expectedApiKey))
            {
                var provided = req.Headers["X-Api-Key"] ?? "";
                if (!FixedTimeEquals(provided, expectedApiKey))
                {
                    await WriteJsonAsync(res, 401, new { error = "Unauthorized: X-Api-Key tidak valid." });
                    return;
                }
            }

            TriggerJob? job = null;
            if (path.StartsWith("/trigger/reply/", StringComparison.OrdinalIgnoreCase))
            {
                var rawId = path["/trigger/reply/".Length..].Trim().Trim('{', '}');
                if (!Guid.TryParse(rawId, out var parsedGuid))
                {
                    await WriteJsonAsync(res, 400, new { error = $"activityId bukan GUID valid: '{rawId}'" });
                    return;
                }
                job = new TriggerJob("reply", parsedGuid.ToString("D"));
            }
            else if (path.Equals("/trigger/dm", StringComparison.OrdinalIgnoreCase))
                job = new TriggerJob("dm", null);
            else if (path.Equals("/trigger/mention", StringComparison.OrdinalIgnoreCase))
                job = new TriggerJob("mention", null);
            else if (path.Equals("/trigger/sync", StringComparison.OrdinalIgnoreCase))
                job = new TriggerJob("sync", null);
            else if (path.Equals("/trigger/sync-mentions", StringComparison.OrdinalIgnoreCase))
                job = new TriggerJob("sync-mentions", null);

            if (job is null)
            {
                await WriteJsonAsync(res, 404, new { error = $"Endpoint tidak dikenal: {path}" });
                return;
            }

            var dedupeKey = $"{job.Value.Kind}:{job.Value.ActivityId ?? "*"}";
            if (!inFlight.TryAdd(dedupeKey, 0))
            {
                await WriteJsonAsync(res, 202, new { accepted = true, deduplicated = true, job = dedupeKey });
                return;
            }

            if (!writer.TryWrite(job.Value))
            {
                inFlight.TryRemove(dedupeKey, out _);
                await WriteJsonAsync(res, 503, new { error = "Antrean sedang penuh, coba lagi sebentar." });
                return;
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Trigger diterima -> {dedupeKey}");
            await WriteJsonAsync(res, 202, new { accepted = true, job = dedupeKey });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[serve/http error] {ex.Message}");
            try { await WriteJsonAsync(res, 500, new { error = ex.Message }); } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    private static async Task RunWorkerAsync(
        ChannelReader<TriggerJob> reader,
        ConcurrentDictionary<string, byte> inFlight,
        string crmConnStr,
        bool verbose,
        CancellationToken ct)
    {
        await foreach (var job in reader.ReadAllAsync(CancellationToken.None))
        {
            var dedupeKey = $"{job.Kind}:{job.ActivityId ?? "*"}";
            try
            {
                if (ct.IsCancellationRequested) break;
                switch (job.Kind)
                {
                    case "reply" when job.ActivityId is not null:
                        await DispatchSingleReplyAsync(crmConnStr, job.ActivityId, verbose);
                        break;
                    case "dm":
                        await ChatReplyCommands.SendRepliesAsync(verbose ? new[] { "send-replies", "--verbose" } : new[] { "send-replies" });
                        break;
                    case "mention":
                        await MentionSyncCommands.SendRepliesAsync(verbose ? new[] { "send-mention-replies", "--verbose" } : new[] { "send-mention-replies" });
                        break;
                    case "sync":
                        await ChatSyncCommands.SyncAsync(verbose ? new[] { "sync", "--verbose" } : new[] { "sync" });
                        break;
                    case "sync-mentions":
                        await MentionSyncCommands.SyncAsync(verbose ? new[] { "sync-mentions", "--verbose" } : new[] { "sync-mentions" });
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] [serve/worker error {dedupeKey}] {ex.Message}");
            }
            finally
            {
                inFlight.TryRemove(dedupeKey, out _);
            }
        }
    }

    private static async Task DispatchSingleReplyAsync(string crmConnStr, string activityId, bool verbose)
    {
        using var crm = new CrmWebApi(crmConnStr);
        using var doc = await crm.GetAsync($"bi_twitters({activityId})?$select=activityid,bi_isdirectmessage");
        var isDm = doc.RootElement.TryGetProperty("bi_isdirectmessage", out var dmEl) &&
                   dmEl.ValueKind == JsonValueKind.True;

        if (isDm)
            await ChatReplyCommands.ProcessSingleReplyAsync(crm, activityId, verbose);
        else
            await MentionSyncCommands.ProcessSingleReplyAsync(crm, activityId, verbose);
    }

    private static string ParsePrefix(string[] args)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--prefix")
                return NormalizePrefix(args[i + 1]);
        }
        var envUrl = Environment.GetEnvironmentVariable("TRIGGER_LISTEN_URL");
        return NormalizePrefix(string.IsNullOrWhiteSpace(envUrl) ? "http://127.0.0.1:5080/" : envUrl);
    }

    private static string NormalizePrefix(string url) => url.EndsWith('/') ? url : url + "/";

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int statusCode, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        res.StatusCode = statusCode;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }
}
