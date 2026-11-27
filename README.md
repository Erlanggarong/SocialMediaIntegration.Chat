# SocialMediaIntegration.Chat

Aplikasi console **.NET 8 (Windows x64)** untuk mengintegrasikan kanal **Direct Message X (Twitter)** — baik **DM terenkripsi E2EE (X Chat)** maupun **DM tidak terenkripsi (`/2/dm_events`)** — dengan **Dynamics 365 / CRM 9.1 on-premise** (Bank Indonesia / BICARA).

---

## Arsitektur Singkat

```text
X API v2 (OAuth 1.0a)                                Dynamics CRM On-Prem (Web API + NTLM)
 ├─ X Chat E2EE (/2/chat/...)   ──► ChatXdk (FFI) ──┐
 └─ DM Plaintext (/2/dm_events) ────────────────────┼──► CrmChatSink ──► bi_twitters & bi_precontacts
                                                    │
CRM Antrian (bi_twitters.bi_replybody) ─────────────┴──► ChatReplyCommands ──► E2EE / Fallback Plaintext
```

### Struktur Kode
| File | Tanggung Jawab |
|---|---|
| `Program.cs` | Entry point CLI, routing perintah, dan `--help`. |
| `ChatSyncCommands.cs` | Perintah `pull` & `sync` (tarik gabungan E2EE + plaintext → CRM `bi_twitter`). |
| `MentionSyncCommands.cs` | Perintah `sync-mentions` (tarik mention publik `GET /2/users/{id}/mentions` → CRM `bi_twitter`). |
| `ChatReplyCommands.cs` | Perintah `send` & `send-replies` (kirim DM & antrian balasan CRM → X, otomatis E2EE atau fallback plaintext). |
| `TriggerServerCommand.cs` | Perintah `serve` — internal HTTP trigger listener (real-time bridge untuk Plugin CRM v9.0). |
| `crm-plugin-sample/XChatReplyTriggerPlugin.cs` | Contoh Plugin Dynamics CRM 9.0 (.NET Framework 4.6.2) siap registrasi yang memanggil mode `serve`. |
| `crm-webresource/xchat_inbox.html` | Halaman **X Chat & Mention Inbox** (HTML Web Resource mandiri untuk Dashboard/Sitemap CRM v9.0). |
| `KeyManagementCommands.cs` | Perintah `inspect-keys`, `unlock-pin` (Juicebox PIN), `keygen`, dan `register-keys`. |
| `DiagnosticsCommands.cs` | Perintah uji offline: `check-native`, `check-keystore`, dan `verify-keys`. |
| `KeyStore.cs` | Penyimpanan kunci privat & `ConversationKeys` terenkripsi **Windows DPAPI (`CurrentUser`)** di `%LOCALAPPDATA%\BICARA\SocialMediaIntegration.Chat\chat_keys.dat`. |
| `OAuth1.cs` | Penandatanganan OAuth 1.0a (HMAC-SHA1), masking prompt secret, dan retry terkendali (429/5xx). |
| `XApiChatClient.cs` | Klien REST X API v2 (X Chat E2EE, public keys + `juicebox_config`, `/2/dm_events`, `/2/dm_conversations`, `/2/users` batch 100 ID). |
| `CrmWebApi.cs` | Klien REST Dynamics CRM on-premise (`/api/data/v9.0/`) dengan autentikasi NTLM, paging `@odata.nextLink`, dan resolusi relasi lookup dari `$metadata`. |
| `CrmChatSink.cs` | Mapping `ChatMessage` (E2EE & plaintext) ke entitas `bi_twitters`, deduplikasi `bi_twittid`, `bi_precontacts`, dan `bi_twitter_activity_parties`. |
| `CrmUtility.cs` | Helper konversi timestamp ISO 8601 & epoch milliseconds UTC. |

---

## Daftar Perintah CLI

Jalankan `SocialMediaIntegration.Chat.exe --help` (atau tanpa argumen) untuk melihat bantuan lengkap.

### Operasional Harian
| Perintah | Alias Lama | Fungsi | Opsi |
|---|---|---|---|
| `sync` | `sync-chat` | Tarik DM (E2EE + plaintext) inkremental (watermark `X Last Sync` di `new_configuration`) & simpan ke CRM `bi_twitter` | `--dry-run`, `--full`, `--verbose`, `--encrypted-only`, `--plaintext-only` |
| `sync-mentions` | — | Tarik mention publik (`/2/users/{id}/mentions`, watermark `X Mention Last Sync`) & simpan ke CRM `bi_twitter` | `--dry-run`, `--full`, `--verbose` |
| `send-replies` | — | Kirim antrian balasan DM CRM (`bi_isdirectmessage=true`, `bi_replybody` + lampiran Notes) ke X (otomatis E2EE / fallback plaintext) | `--dry-run`, `--verbose` |
| `send-mention-replies` | — | Kirim antrian balasan mention CRM (`bi_isdirectmessage=false`, `bi_inreplystatusid`, `bi_replybody` + lampiran Notes) via `POST /2/tweets` | `--dry-run`, `--verbose` |
| `serve` | — | Internal HTTP trigger service (real-time dari Plugin CRM v9.0; balas `202 Accepted` + antrean serial) | `--prefix <url>`, `--verbose` |

### Operasi & Uji Manual
| Perintah | Alias Lama | Fungsi | Opsi |
|---|---|---|---|
| `pull` | `pull-chat` | Tarik & cetak DM ke console tanpa menulis CRM | `--verbose`, `--encrypted-only`, `--plaintext-only` |
| `send <userId> [pesan]` | `send-chat` | Kirim 1 DM langsung ke user X | `--file <path>` (opsional, bisa diulang) |
| `reply-mention <tweetId> [pesan]` | — | Balas 1 tweet/mention langsung via `POST /2/tweets` | `--file <path>` (maks 4 gambar) |

### Manajemen Kunci E2EE (Bootstrap)
| Perintah | Fungsi |
|---|---|
| `inspect-keys` | *(Read-only)* Cek public key terdaftar di X API & ketersediaan `juicebox_config`. |
| `unlock-pin` | Pulihkan kunci aplikasi resmi X memakai PIN X Chat (Juicebox) → simpan DPAPI (blob lama di-backup otomatis). |
| `keygen` | Buat keypair lokal baru (SPKI) & simpan terproteksi DPAPI. |
| `verify-keys` | Uji konsistensi kriptografi `chat_keys.dat` lokal tanpa jaringan. |
| `register-keys` | Registrasi public key lokal ke X API (kuota 5 req/24 jam; reconcile via GET terlebih dahulu). |

### Diagnostik Offline
| Perintah | Alias Lama | Fungsi |
|---|---|---|
| `check-native` | `fase1` | Uji load native `chat_xdk_ffi.dll`, round-trip kunci 64B, & dekripsi `fixtures/sdk_vectors.json`. |
| `check-keystore` | `fase2` | Uji DPAPI & `ConversationKeys` pada folder `%TEMP%` terisolasi (tidak menyentuh `chat_keys.dat` asli). |

---

## Environment Variables

| Variable | Wajib | Keterangan |
|---|---|---|
| `X_CONSUMER_KEY`, `X_CONSUMER_SECRET`, `X_ACCESS_TOKEN`, `X_TOKEN_SECRET` | Ya* | Kredensial OAuth 1.0a X API (*bila kosong, ditanyakan interaktif di console; secret tanpa echo). |
| `CRM_CONNECTION_STRING` | Untuk CRM | Format: `AuthType=AD;Url=http://<host>:<port>/<ORG>;Domain=<DOM>;Username=<user>;Password=<pass>` |
| `CRM_SEND_REPLIES_SINCE` | Opsional | Batas tanggal (`yyyy-MM-dd`) agar record antrian historis lama tidak terkirim saat `send-replies`. |
| `CRM_SYNC_CONFIG_NAME` | Opsional | Nama record watermark di `new_configuration` (default: `X Last Sync`). |
| `CRM_MAX_ATTACHMENT_MB` | Opsional | Batas ukuran lampiran maksimal dalam MB (default: `15`). |
| `CRM_SKIP_ATTACHMENTS` | Opsional | Set `true` bila ingin menonaktifkan unduh/unggah lampiran ke CRM `annotations`. |
| `X_DM_EVENTS_MAX_PAGES` | Opsional | Maks halaman pagination `/2/dm_events` per run (default `5` = maks 500 event terbaru; `0` = semua). |
| `CRM_REPLY_BODY_ATTRIBUTE` | Opsional | Override nama atribut isi balasan CRM (default: `bi_replybody`). |
| `CRM_REPLY_DIRECTION`, `CRM_REPLY_STATUS_OPEN`, `CRM_REPLY_STATUS_SENT`, `CRM_REPLY_STATUS_INACTIVE` | Opsional | Override kode option set CRM (default: `157570001`, `1`, `157570001`, `statecode=1`). |

---

## Build & Publish

```powershell
# Build & uji offline lokal
dotnet build
.\bin\Debug\net8.0\SocialMediaIntegration.Chat.exe check-native
.\bin\Debug\net8.0\SocialMediaIntegration.Chat.exe check-keystore

# Publish produksi (Windows x64 self-contained)
dotnet publish -c Release -r win-x64 --self-contained -o C:\Apps\SocialMediaIntegration.Chat
```

Untuk panduan deploy, bootstrap kunci, penjadwalan Task Scheduler, dan troubleshooting produksi, lihat **`RUNBOOK-PRODUKSI.md`**.
