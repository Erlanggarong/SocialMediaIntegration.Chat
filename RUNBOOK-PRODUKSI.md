# Runbook Operasional — SocialMediaIntegration.Chat (Kanal DM X)

Dokumen ini adalah panduan operasional langkah-demi-langkah untuk menjalankan aplikasi console
`SocialMediaIntegration.Chat` (.NET 8) di environment produksi Bank Indonesia (BICARA).
Untuk ringkasan arsitektur kode & daftar lengkap perintah CLI, lihat `README.md`.

---

## 0. Prasyarat di Server Produksi

| Item | Ketentuan |
|---|---|
| OS | **Windows x64** (native `chat_xdk_ffi.dll` hanya berjalan di x64, bukan ARM/32-bit) |
| Runtime | Tidak wajib jika publish `--self-contained` (atau .NET 8 runtime bila framework-dependent) |
| Akun jalan | **Service account khusus** (bukan akun admin pribadi). File `chat_keys.dat` terenkripsi DPAPI `CurrentUser` — Task Scheduler **harus** berjalan sebagai user Windows yang sama dengan saat bootstrap kunci |
| Firewall | Outbound HTTPS ke `api.x.com` (dan realm Juicebox saat `unlock-pin`) |
| Kredensial X | App resmi BI, permission **"Read and write and Direct message"**, token dari **akun resmi BI** |
| Akses CRM | Akun AD/NTLM ke CRM on-premise Web API (`/api/data/v9.0/`) dengan hak read/create/update `bi_twitter`, `bi_precontact`, `new_configuration`, dan create `annotation` (untuk lampiran) |

---

## 1. Deploy Aplikasi

```powershell
# Opsi A (disarankan): self-contained (tidak perlu instal .NET 8 runtime di server)
dotnet publish -c Release -r win-x64 --self-contained -o C:\Apps\SocialMediaIntegration.Chat

# Opsi B: framework-dependent (server harus punya .NET 8 runtime)
dotnet publish -c Release -o C:\Apps\SocialMediaIntegration.Chat
```

Pastikan `SocialMediaIntegration.Chat.exe`, `chat_xdk_ffi.dll`, dan folder `fixtures/` ikut ter-deploy.

---

## 2. Environment Variables

Setel di level **Task Scheduler / service wrapper** (jangan disimpan plaintext di git atau repo):

```powershell
# Kredensial OAuth 1.0a akun X resmi BI
$env:X_CONSUMER_KEY       = "<API Key>"
$env:X_CONSUMER_SECRET    = "<API Secret>"
$env:X_ACCESS_TOKEN       = "<Access Token>"
$env:X_TOKEN_SECRET       = "<Access Token Secret>"

# Koneksi CRM on-premise (Web API + NTLM)
$env:CRM_CONNECTION_STRING = "AuthType=AD;Url=http://<server>:<port>/<ORG>;Domain=<DOMAIN>;Username=<user>;Password=<pass>"

# Pengaman antrian balasan (wajib diisi tanggal go-live agar record historis lama tidak ikut terkirim)
$env:CRM_SEND_REPLIES_SINCE = "2026-11-24"

# Opsional — batas halaman DM plaintext (/2/dm_events) per eksekusi (default 5; 0 = semua halaman)
# $env:X_DM_EVENTS_MAX_PAGES    = "5"

# Opsional — override nama field / kode status antrian CRM
# $env:CRM_REPLY_BODY_ATTRIBUTE  = "bi_replybody"
# $env:CRM_REPLY_DIRECTION       = "157570001"
# $env:CRM_REPLY_STATUS_OPEN     = "1"
# $env:CRM_REPLY_STATUS_SENT     = "157570001"
# $env:CRM_REPLY_STATUS_INACTIVE = "1"            # statecode Completed/Inactive setelah terkirim
```

---

## 3. Bootstrap Kunci E2EE (Sekali Saja — Jalankan Sebagai Service Account)

Ada dua jalur bootstrap. **Jalur A (`unlock-pin`) disarankan** agar pesan yang dibalas dari aplikasi resmi X (HP/web) tetap terbaca oleh CRM.

### Jalur A (Disarankan): Berbagi Kunci dengan Aplikasi Resmi X via Juicebox PIN

```powershell
cd C:\Apps\SocialMediaIntegration.Chat

# 3a. Cek apakah akun mengembalikan juicebox_config (read-only, aman)
.\SocialMediaIntegration.Chat.exe inspect-keys

# 3b. Pulihkan kunci aplikasi resmi memakai PIN X Chat akun resmi
#     (hati-hati: Juicebox membatasi salah PIN; blob lama otomatis di-backup)
.\SocialMediaIntegration.Chat.exe unlock-pin

# 3c. Verifikasi konsistensi key blob lokal (offline)
.\SocialMediaIntegration.Chat.exe verify-keys
```

### Jalur B (Alternatif): Buat Kunci Terpisah Khusus API

> **Perhatian kuota:** Endpoint `POST /2/users/{id}/public_keys` dibatasi **5 request / 24 jam / user**.
> Program tidak melakukan retry otomatis pada endpoint ini.

```powershell
cd C:\Apps\SocialMediaIntegration.Chat

# 3a. Generate keypair SPKI lokal & simpan terproteksi DPAPI
.\SocialMediaIntegration.Chat.exe keygen

# 3b. WAJIB sebelum registrasi: uji konsistensi lokal tanpa jaringan (harus 0 kegagalan)
.\SocialMediaIntegration.Chat.exe verify-keys

# 3c. Registrasi ke X API (sekali saja; program reconcile via GET sebelum POST)
.\SocialMediaIntegration.Chat.exe register-keys
```

---

## 4. Verifikasi Awal (Onboarding)

```powershell
# Uji native DLL & dekripsi fixture (aman, offline)
.\SocialMediaIntegration.Chat.exe check-native

# Uji DPAPI & ConversationKeys (aman, offline, memakai folder %TEMP% terisolasi)
.\SocialMediaIntegration.Chat.exe check-keystore

# Tarik & dekripsi DM (E2EE + plaintext) ke console tanpa menulis CRM
.\SocialMediaIntegration.Chat.exe pull --verbose

# Simulasi sync ke CRM tanpa menulis data
.\SocialMediaIntegration.Chat.exe sync --dry-run

# Cek kandidat antrian balasan CRM tanpa mengirim ke X
.\SocialMediaIntegration.Chat.exe send-replies --dry-run
```

---

## 5. Operasional Harian & Penjadwalan

### 5a. Tarik DM Masuk & Keluar ke CRM (`sync`)

```powershell
# Mode standar: tarik inkremental (melanjutkan dari watermark 'X Last Sync' di new_configuration)
.\SocialMediaIntegration.Chat.exe sync

# Tarik ulang penuh (mengabaikan watermark 'X Last Sync'):
.\SocialMediaIntegration.Chat.exe sync --full

# Opsi filter sumber bila diperlukan:
.\SocialMediaIntegration.Chat.exe sync --encrypted-only
.\SocialMediaIntegration.Chat.exe sync --plaintext-only
```

- **Watermark inkremental (`new_configuration`)**:
  - Membaca record aktif `new_name = "X Last Sync"` (kolom `new_value`, format UTC ISO 8601 `yyyy-MM-ddTHH:mm:ssZ`).
  - Memakai **buffer aman 2 menit mundur** agar tidak ada pesan terlewat akibat jeda indexing/jam server; `/2/dm_events` otomatis berhenti pagination saat mencapai cutoff, dan pesan lama dilewati sebelum query CRM.
  - Setelah `sync` sukses penuh (0 error), `new_value` diperbarui otomatis (atau dibuat bila record belum ada). Untuk menarik ulang dari tanggal tertentu, cukup ubah nilai `Value` pada record `X Last Sync` di CRM atau jalankan `sync --full`.
- **Lampiran media (E2EE & plaintext)**: saat `sync` menulis ke CRM, lampiran diunduh (`/2/chat/media/{id}/{hash}` + dekripsi `DecryptFile` untuk E2EE, atau URL media untuk plaintext) lalu diunggah sebagai **Note/Attachment (`annotation`)** pada record `bi_twitter`. Batas ukuran default `15 MB` (`CRM_MAX_ATTACHMENT_MB`), dapat dinonaktifkan dengan `CRM_SKIP_ATTACHMENTS=true`.
- Deduplikasi akhir tetap aktif via `bi_twittid` (idempotent, aman dijalankan berulang).
- Kegagalan di salah satu sumber (E2EE atau plaintext) diisolasi agar sumber lainnya tetap berjalan.

### 5b. Tarik Mention Publik ke CRM (`sync-mentions`)

```powershell
# Mode standar: tarik mention baru sejak watermark 'X Mention Last Sync' (filter server-side start_time)
.\SocialMediaIntegration.Chat.exe sync-mentions

# Simulasi tanpa menulis CRM / tarik ulang penuh:
.\SocialMediaIntegration.Chat.exe sync-mentions --dry-run
.\SocialMediaIntegration.Chat.exe sync-mentions --full
```

- Membaca `BI Twitter Id` dari `new_configuration` bila tersedia (otomatis fallback ke `GET /2/users/me`).
- Menyimpan ke `bi_twitter` (`bi_isdirectmessage = false`, `bi_direction = 157570000`, `bi_media1..4`, `bi_inreplystatusid`, `bi_precontact`, `bi_twitter_activity_parties`), dengan dedupe `bi_twittid` dan watermark `X Mention Last Sync`.

### 5c. Kirim Antrian Balasan dari CRM ke X (`send-replies`)

```powershell
.\SocialMediaIntegration.Chat.exe send-replies
```

- Memproses `bi_twitters` dengan `bi_isdirectmessage = true`, `bi_direction = 157570001` (Out), `statecode = 0` (Open), `statuscode = 1` (Open), dan `bi_replybody != null` (+ cutoff `CRM_SEND_REPLIES_SINCE`).
- **Otomatis hybrid**: jika penerima punya public key X Chat → kirim **E2EE** (`/2/chat/.../messages`); jika tidak punya → fallback **plaintext** (`/2/dm_conversations/with/{id}/messages`).
- **Lampiran gambar/file balasan**: bila agen melampirkan file di tab **Notes (`annotation`)** pada record `bi_twitter` balasan, `send-replies` otomatis mengunggah & mengenkripsi file tersebut (E2EE via `/2/chat/media/upload/...` + `EncryptFile`, atau plaintext via `/2/media/upload/...`) lalu mengirimkannya bersama teks `bi_replybody`.
- Setelah terkirim: mengisi `bi_twittid`, `bi_tweetlink`, `bi_createdat`, `description`, mempertahankan `bi_replybody` (tidak dikosongkan; anti-kirim ulang dijaga via `bi_twittid eq null` + `statecode = 0`), dan mengubah `statecode` → `1` (Completed/Inactive) serta `statuscode` → **Sent** (`157570001`).

### 5d. Kirim Antrian Balasan Mention dari CRM ke X (`send-mention-replies`)

```powershell
# Cek kandidat antrian balasan mention tanpa mengirim
.\SocialMediaIntegration.Chat.exe send-mention-replies --dry-run

# Kirim balasan mention ke X via POST /2/tweets (+ lampiran Notes bila ada)
.\SocialMediaIntegration.Chat.exe send-mention-replies
```

- Memproses `bi_twitters` dengan `bi_isdirectmessage = false`, `bi_direction = 157570001` (Out), `statecode = 0` (Open), `statuscode = 1` (Open), dan `bi_replybody != null` (+ cutoff `CRM_SEND_REPLIES_SINCE`).
- Menggunakan `bi_inreplystatusid` sebagai `reply.in_reply_to_tweet_id` (dan mengunggah lampiran Notes `annotation` maks 4 gambar via `/2/media/upload/...`).
- Setelah terkirim: mengisi `bi_twittid`, `bi_tweetlink`, `bi_createdat`, `description`, mempertahankan `bi_replybody` (tidak dikosongkan), dan mengubah `statecode` → `1` serta `statuscode` → **Sent** (`157570001`).

### 5e. Mode Real-Time via Plugin CRM v9.0 (`serve`) *(Opsional)*

Jika ingin balasan DM & Mention terkirim **seketika (< 1 detik)** saat agen klik Save/Send di CRM:

1. **Setel Environment Variables** (`TRIGGER_LISTEN_URL`, `TRIGGER_API_KEY`) untuk service account `BICARA\SVC-XChat`:
   ```powershell
   $env:TRIGGER_LISTEN_URL = "http://+:5080/"    # atau http://127.0.0.1:5080/ jika satu server dengan CRM
   $env:TRIGGER_API_KEY    = "<secret-internal-panjang>"
   ```
   *(Bila memakai `http://+:5080/` lintas server, jalankan sekali sebagai Administrator: `netsh http add urlacl url=http://+:5080/ user="BICARA\SVC-XChat"`.)*

2. **Pilih salah satu cara agar `serve` otomatis menyala saat server boot (tanpa login):**

   - **Opsi 1 — Windows Service via NSSM *(disarankan untuk produksi)*:**
     ```powershell
     nssm install BICARA-XChat-Serve "C:\Apps\SocialMediaIntegration.Chat\SocialMediaIntegration.Chat.exe" "serve"
     nssm set BICARA-XChat-Serve AppDirectory "C:\Apps\SocialMediaIntegration.Chat"
     nssm set BICARA-XChat-Serve ObjectName "BICARA\SVC-XChat" "<password>"
     nssm set BICARA-XChat-Serve Start SERVICE_AUTO_START
     nssm start BICARA-XChat-Serve
     ```
   - **Opsi 2 — Task Scheduler `At Startup` *(jika tidak boleh instal NSSM)*:**
     *(Wajib set `-ExecutionTimeLimit ([TimeSpan]::Zero)` agar proses tidak dimatikan otomatis setelah 3 hari.)*
     ```powershell
     $serveAction = New-ScheduledTaskAction `
         -Execute "C:\Apps\SocialMediaIntegration.Chat\SocialMediaIntegration.Chat.exe" `
         -Argument "serve" -WorkingDirectory "C:\Apps\SocialMediaIntegration.Chat"
     $serveTrigger = New-ScheduledTaskTrigger -AtStartup
     $serveSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
         -RestartInterval (New-TimeSpan -Minutes 1) -RestartCount 999 -MultipleInstances IgnoreNew
     $principal = New-ScheduledTaskPrincipal -UserId "BICARA\SVC-XChat" -LogonType ServiceAccount -RunLevel Limited
     Register-ScheduledTask -TaskName "BICARA-XChat-Serve" `
         -Action $serveAction -Trigger $serveTrigger -Settings $serveSettings -Principal $principal
     ```

3. **Register Plugin CRM v9.0**: compile `crm-plugin-sample\XChatReplyTriggerPlugin.cs` (.NET Framework 4.6.2), lalu register pada entity `bi_twitter` (Post-Operation **Asynchronous** Create & Update `bi_replybody`) dengan konfigurasi `http://<host-service>:5080|<TRIGGER_API_KEY>`.
4. Tetap biarkan Task Scheduler (`send-replies` & `send-mention-replies`) aktif sebagai jaring pengaman bila service sempat restart.

### 5f. Cutover dari Sistem Lama
- Saat `sync` (gabungan E2EE + plaintext) aktif di produksi, **matikan job penarikan DM pada `XApiGetDM` lama** agar tidak terjadi race condition.
- Saat `sync-mentions` aktif di produksi, **matikan job penarikan mention pada `XApiGetMentionList` lama**.
- Saat `send-replies` / `send-mention-replies` aktif di produksi, **nonaktifkan plugin `TwitterPostDMPlugin` lama** agar satu balasan agen tidak terkirim dua kali.

### 5g. Penjadwalan Berkala (Task Scheduler)

```powershell
$exe       = "C:\Apps\SocialMediaIntegration.Chat\SocialMediaIntegration.Chat.exe"
$workDir   = "C:\Apps\SocialMediaIntegration.Chat"
$principal = New-ScheduledTaskPrincipal -UserId "BICARA\SVC-XChat" -LogonType ServiceAccount -RunLevel Limited

# Task 1: Tarik DM X -> CRM tiap 5 menit
$syncTrigger = New-ScheduledTaskTrigger -Once -At 06:00 -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration ([TimeSpan]::MaxValue)
Register-ScheduledTask -TaskName "BICARA-XChat-Sync" `
    -Action (New-ScheduledTaskAction -Execute $exe -Argument "sync" -WorkingDirectory $workDir) `
    -Trigger $syncTrigger -Principal $principal

# Task 2: Tarik Mention X -> CRM tiap 5 menit
$mentionTrigger = New-ScheduledTaskTrigger -Once -At 06:02 -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration ([TimeSpan]::MaxValue)
Register-ScheduledTask -TaskName "BICARA-XChat-SyncMentions" `
    -Action (New-ScheduledTaskAction -Execute $exe -Argument "sync-mentions" -WorkingDirectory $workDir) `
    -Trigger $mentionTrigger -Principal $principal

# Task 3: Kirim balasan DM CRM -> X tiap 2 menit (atau sebagai jaring pengaman mode 'serve')
$replyTrigger = New-ScheduledTaskTrigger -Once -At 06:01 -RepetitionInterval (New-TimeSpan -Minutes 2) -RepetitionDuration ([TimeSpan]::MaxValue)
Register-ScheduledTask -TaskName "BICARA-XChat-SendReplies" `
    -Action (New-ScheduledTaskAction -Execute $exe -Argument "send-replies" -WorkingDirectory $workDir) `
    -Trigger $replyTrigger -Principal $principal

# Task 4: Kirim balasan Mention CRM -> X tiap 2 menit (atau sebagai jaring pengaman mode 'serve')
$mentionReplyTrigger = New-ScheduledTaskTrigger -Once -At 06:03 -RepetitionInterval (New-TimeSpan -Minutes 2) -RepetitionDuration ([TimeSpan]::MaxValue)
Register-ScheduledTask -TaskName "BICARA-XChat-SendMentionReplies" `
    -Action (New-ScheduledTaskAction -Execute $exe -Argument "send-mention-replies" -WorkingDirectory $workDir) `
    -Trigger $mentionReplyTrigger -Principal $principal
```

*(Alias perintah lama `sync-chat`, `pull-chat`, `send-chat`, `fase1`, `fase2` tetap didukung untuk kompatibilitas mundur.)*

---

## 6. Troubleshooting

| Gejala | Penyebab Kemungkinan | Tindakan |
|---|---|---|
| `DllNotFoundException` saat startup | Bukan Windows x64 atau `chat_xdk_ffi.dll` tidak ikut publish | Publish ulang `-r win-x64 --self-contained` |
| `CryptographicException` saat buka `chat_keys.dat` | Dijalankan dengan user Windows berbeda dari saat `unlock-pin`/`keygen` | Pastikan Task Scheduler memakai service account yang sama |
| `403 oauth1-permissions` | Permission app bukan "Read and write and Direct message", atau token dibuat sebelum permission diubah | Ubah permission di developer portal X, **regenerate token**, update env var |
| `429` pada `register-keys` | Kuota 5 request / 24 jam habis | Tunggu 24 jam penuh; jangan retry manual |
| Sebagian pesan gagal dekripsi di `pull --verbose` | Kunci di `chat_keys.dat` berbeda dari kunci aplikasi resmi X | Jalankan `inspect-keys` lalu `unlock-pin` dengan PIN X Chat akun resmi |
| `sync` / `send-replies` gagal konek CRM | `CRM_CONNECTION_STRING` salah atau password akun AD kedaluwarsa | Cek format `AuthType=AD;Url=...;Domain=...;Username=...;Password=...` dan kredensial AD |

---

## 7. Rotasi Kunci

- **Jika memakai Jalur A (`unlock-pin`)** dan akun resmi mengganti/reset kunci E2EE di aplikasi X: jalankan ulang `unlock-pin` sebagai service account (blob lama otomatis di-backup).
- **Jika memakai Jalur B (`keygen` + `register-keys`)**: backup `chat_keys.dat` terlebih dahulu, lalu jalankan `keygen` → `verify-keys` → `register-keys` (ketik `ROTASI` saat konfirmasi). Pesan historis sebelum rotasi tidak dapat didekripsi dengan kunci baru.

---

## 8. Cakupan Pengembangan Lanjutan

- **Percakapan grup (multi-participant)** — saat ini mapping `bi_twitter` & `CanonicalConversationId` khusus percakapan 1:1.
