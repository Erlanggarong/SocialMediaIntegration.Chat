// ============================================================
// SocialMediaIntegration.Chat — Entry Point CLI
// Integrasi Kanal Direct Message X (E2EE & Plaintext) <-> Dynamics CRM
// Bank Indonesia (BICARA)
// ============================================================

using SocialMediaIntegration.Chat;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var command = args[0].ToLowerInvariant();
return command switch
{
    // Operasional harian
    "sync" or "sync-chat" => await ChatSyncCommands.SyncAsync(args),
    "sync-mentions" => await MentionSyncCommands.SyncAsync(args),
    "send-replies" => await ChatReplyCommands.SendRepliesAsync(args),
    "send-mention-replies" => await MentionSyncCommands.SendRepliesAsync(args),
    "serve" => await TriggerServerCommand.RunAsync(args),

    // Operasi & uji manual
    "pull" or "pull-chat" => await ChatSyncCommands.PullAsync(args),
    "send" or "send-chat" => await ChatReplyCommands.SendAsync(args),
    "reply-mention" => await MentionSyncCommands.ReplyAsync(args),

    // Manajemen kunci E2EE (bootstrap)
    "inspect-keys" => await KeyManagementCommands.InspectKeysAsync(),
    "unlock-pin" => await KeyManagementCommands.UnlockPinAsync(),
    "keygen" => KeyManagementCommands.Keygen(),
    "verify-keys" => DiagnosticsCommands.VerifyKeys(),
    "register-keys" => await KeyManagementCommands.RegisterKeysAsync(),

    // Diagnostik offline
    "check-native" or "fase1" => DiagnosticsCommands.CheckNative(),
    "check-keystore" or "fase2" => DiagnosticsCommands.CheckKeyStore(),

    _ => Unknown(args[0]),
};

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"Perintah tidak dikenal: '{cmd}'. Jalankan '--help' untuk daftar perintah.");
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
    SocialMediaIntegration.Chat — Integrasi DM X (E2EE + Plaintext) <-> Dynamics CRM (BICARA)

    Pemakaian:
      SocialMediaIntegration.Chat.exe <perintah> [opsi]

    Operasional Harian:
      sync            [opsi]        Tarik DM (E2EE + plaintext) dan simpan ke CRM bi_twitter
                                    (alias: sync-chat; memakai watermark 'X Last Sync' di new_configuration)
                                    Opsi: --dry-run | --full | --verbose | --encrypted-only | --plaintext-only
      sync-mentions   [opsi]        Tarik mention publik X (/2/users/{id}/mentions) ke CRM bi_twitter
                                    (memakai watermark 'X Mention Last Sync' di new_configuration)
                                    Opsi: --dry-run | --full | --verbose
      send-replies    [opsi]        Kirim antrian balasan DM dari CRM (bi_replybody) ke X
                                    (otomatis E2EE, fallback plaintext bila penerima non-E2EE)
                                    Opsi: --dry-run | --verbose
      send-mention-replies [opsi]   Kirim antrian balasan mention dari CRM (bi_isdirectmessage=false)
                                    via POST /2/tweets (+ lampiran Notes opsional)
                                    Opsi: --dry-run | --verbose
      serve           [opsi]        Jalankan internal HTTP trigger listener (real-time untuk Plugin CRM)
                                    Opsi: --prefix http://127.0.0.1:5080/ | --verbose

    Operasi & Uji Manual:
      pull            [opsi]        Tarik & cetak DM (E2EE + plaintext) tanpa menulis ke CRM
                                    (alias: pull-chat)
                                    Opsi: --verbose | --encrypted-only | --plaintext-only
      send <userId> [pesan]         Kirim 1 DM langsung ke user X (alias: send-chat)
                                    Opsi: --file <path> (bisa diulang untuk melampirkan gambar/file)
      reply-mention <tweetId> [msg] Balas 1 tweet/mention langsung ke X via POST /2/tweets
                                    Opsi: --file <path> (maks 4 gambar)

    Manajemen Kunci E2EE (Bootstrap):
      inspect-keys                  Cek public key terdaftar di X API & status juicebox_config (read-only)
      unlock-pin                    Pulihkan kunci aplikasi resmi X via Juicebox PIN -> simpan DPAPI
      keygen                        Buat keypair lokal baru & simpan terproteksi DPAPI
      verify-keys                   Uji konsistensi kriptografi chat_keys.dat lokal (offline)
      register-keys                 Registrasi kunci lokal ke X API (kuota 5 req/24 jam)

    Diagnostik Offline:
      check-native                  Uji load native chat_xdk_ffi.dll & dekripsi fixture (alias: fase1)
      check-keystore                Uji round-trip DPAPI & ConversationKeys terisolasi (alias: fase2)
      help | --help | -h            Tampilkan bantuan ini

    Environment Variables Utama:
      X_CONSUMER_KEY, X_CONSUMER_SECRET, X_ACCESS_TOKEN, X_TOKEN_SECRET
      CRM_CONNECTION_STRING         AuthType=AD;Url=http://<host>/<ORG>;Domain=<DOM>;Username=<U>;Password=<P>
      CRM_SEND_REPLIES_SINCE        Opsional (yyyy-MM-dd): batas tanggal antrian send-replies
      CRM_SYNC_CONFIG_NAME          Opsional (default: "X Last Sync"): nama record watermark di new_configuration
      TRIGGER_LISTEN_URL            Opsional (default: "http://127.0.0.1:5080/"): URL listen mode 'serve'
      TRIGGER_API_KEY               Opsional (disarankan): secret header X-Api-Key untuk mode 'serve'
      X_DM_EVENTS_MAX_PAGES         Opsional (default 5): maks halaman /2/dm_events (0 = semua)
    """);
}
