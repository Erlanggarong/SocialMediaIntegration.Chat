// ============================================================
// XChatReplyTriggerPlugin — Contoh Plugin Dynamics CRM 9.0 (.NET Framework 4.6.2)
//
// Tujuan:
//   Trigger real-time ke service .NET 8 (SocialMediaIntegration.Chat.exe serve)
//   setiap kali agen mengisi balasan DM atau Mention pada entity bi_twitter.
//
// Cara Registrasi di Plugin Registration Tool:
//   1. Compile file ini di proyek Class Library (.NET Framework 4.6.2) yang
//      mereferensikan Microsoft.CrmSdk.CoreAssemblies (v9.0).
//   2. Register Assembly (Isolation Mode: None untuk on-prem, atau Sandbox).
//   3. Register 2 Step pada entity 'bi_twitter':
//      - Step 1: Message = Create, Stage = PostOperation, Execution Mode = Asynchronous
//      - Step 2: Message = Update, Stage = PostOperation, Execution Mode = Asynchronous,
//                Filtering Attributes = bi_replybody
//   4. Isi Unsecure Configuration (atau Secure Configuration) pada kedua step:
//        http://<ip-atau-host-internal>:5080|<TRIGGER_API_KEY>
//      Contoh:
//        http://127.0.0.1:5080|RahasiaInternalBICARA2026
//
// Catatan:
//   - Plugin hanya mengirim POST ringan (timeout 5 detik) dan langsung selesai
//     begitu menerima 202 Accepted (< 50 ms) — tidak pernah blocking UI CRM.
//   - Jika service sedang restart, error dicatat di Trace saja; Task Scheduler
//     (send-replies & send-mention-replies) otomatis menjadi jaring pengaman.
// ============================================================

using System;
using System.Net;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace SocialMediaIntegration.CrmPlugin
{
    public sealed class XChatReplyTriggerPlugin : IPlugin
    {
        private const int DirectionOutgoing = 157570001;
        private readonly string _baseUrl;
        private readonly string _apiKey;

        public XChatReplyTriggerPlugin(string unsecureConfig, string secureConfig)
        {
            var raw = !string.IsNullOrWhiteSpace(secureConfig) ? secureConfig : unsecureConfig;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parts = raw.Split(new[] { '|' }, 2);
                _baseUrl = parts[0].Trim().TrimEnd('/');
                _apiKey = parts.Length > 1 ? parts[1].Trim() : "";
            }
            else
            {
                _baseUrl = "http://127.0.0.1:5080";
                _apiKey = "";
            }
        }

        public void Execute(IServiceProvider serviceProvider)
        {
            var trace = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));

            // Resolusi recordId:
            //   - Create / Update      -> InputParameters["Target"] bertipe Entity
            //   - Bound Custom Action  -> InputParameters["Target"] bertipe EntityReference
            //   - Fallback             -> context.PrimaryEntityId
            Guid recordId = Guid.Empty;
            string entityName = context.PrimaryEntityName;

            if (context.InputParameters.Contains("Target"))
            {
                if (context.InputParameters["Target"] is Entity targetEntity)
                {
                    recordId = targetEntity.Id;
                    entityName = targetEntity.LogicalName;
                }
                else if (context.InputParameters["Target"] is EntityReference targetRef)
                {
                    recordId = targetRef.Id;
                    entityName = targetRef.LogicalName;
                }
            }

            if (recordId == Guid.Empty && context.PrimaryEntityId != Guid.Empty)
            {
                recordId = context.PrimaryEntityId;
            }

            if (recordId == Guid.Empty || !string.Equals(entityName, "bi_twitter", StringComparison.OrdinalIgnoreCase))
            {
                trace.Trace("Skip trigger: Target tidak valid (entity={0}, id={1}, message={2}).",
                    entityName, recordId, context.MessageName);
                return;
            }

            // Pastikan bi_replybody terisi (ambil langsung dari CRM agar selalu up-to-date).
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);

            var full = service.Retrieve("bi_twitter", recordId,
                new ColumnSet("bi_replybody", "bi_twittid", "bi_direction", "statecode"));

            var replyBody = full.GetAttributeValue<string>("bi_replybody");
            var existingTwittId = full.GetAttributeValue<string>("bi_twittid");
            var direction = full.GetAttributeValue<OptionSetValue>("bi_direction")?.Value ?? -1;
            var state = full.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? -1;

            if (state != 0 || direction != DirectionOutgoing ||
                !string.IsNullOrWhiteSpace(existingTwittId) || string.IsNullOrWhiteSpace(replyBody))
            {
                trace.Trace("Skip trigger: state={0}, direction={1}, hasTwittId={2}, replyEmpty={3}",
                    state, direction, !string.IsNullOrWhiteSpace(existingTwittId), string.IsNullOrWhiteSpace(replyBody));
                return;
            }

            var url = $"{_baseUrl}/trigger/reply/{recordId:D}";
            trace.Trace("Triggering real-time X reply -> {0}", url);

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.Timeout = 5000;
                req.ContentLength = 0;
                if (!string.IsNullOrEmpty(_apiKey))
                    req.Headers["X-Api-Key"] = _apiKey;

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    trace.Trace("Trigger response: {0} {1}", (int)resp.StatusCode, resp.StatusDescription);
                }
            }
            catch (Exception ex)
            {
                // Jangan throw: biarkan Task Scheduler (send-replies / send-mention-replies)
                // memproses record ini sebagai fallback bila service sedang restart.
                trace.Trace("Trigger HTTP gagal (fallback ke Task Scheduler): {0}", ex.Message);
            }
        }
    }
}
