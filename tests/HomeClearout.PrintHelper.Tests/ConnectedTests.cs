using HomeClearout.PrintHelper;
using System.Text.Json;

internal static class ConnectedTests
{
    public static void Run(string directory)
    {
        directory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var code = JsonSerializer.Serialize(new PrintConnection(1, "https://example.supabase.co", "sb_publishable_test", "https://home-clearout-buddy.lovable.app", new string('a', 64)));
        var connection = PrintConnection.Parse(code);
        var store = new ConnectionStore(directory); store.Save(connection);
        Check(store.Load() == connection, "DPAPI round trip");
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory, "connection.dpapi"))).Contains(connection.Secret), "Credential encrypted at rest");
        foreach (var invalid in new[] { code.Replace("https://example.supabase.co", "http://example.supabase.co"), code.Replace("https://example.supabase.co", "https://user:pass@example.org"), code.Replace("sb_publishable_test", "sb_secret_forbidden"), "{}" })
        {
            bool rejected = false; try { PrintConnection.Parse(invalid); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid pairing rejected");
        }
        var payload = new PrintPayload(Guid.NewGuid(), Guid.NewGuid(), "HC-0123", "Inventory chair", 150, "Claimed", "Awaiting confirmation");
        using (var handler = new RecordingHandler(connection, payload))
        using (var http = new HttpClient(handler))
        {
            var client = new PrintQueueClient(connection, http);
            var received = client.Claim(Guid.NewGuid(), Guid.NewGuid()).GetAwaiter().GetResult();
            Check(received == payload, "Wire payload maps database field names correctly");
            client.Event(Guid.NewGuid(), Guid.NewGuid(), payload.Id, "Submitted", "Laser").GetAwaiter().GetResult();
            Check(handler.Calls == 2, "RPC transport sends one call per operation");
        }
        Check(payload.Label(connection).Url.EndsWith("/item/" + payload.ItemId), "Real private item QR");
        foreach (var size in LabelSize.Presets)
        {
            var label = (payload with { Height = size.HeightHundredths, Description = new string('W', 60) }).Label(connection);
            using var image = LabelRenderer.Render(label);
            Check(label.InventoryId == payload.Code && label.Description.Length <= 60 && (size.HeightHundredths != 100 || label.Description.Length < 60), "Long descriptions abbreviate legibly without changing ID");
        }
        int prints = 0;
        var queue = new FakeQueue(payload);
        var journal = new PrintJournal(Path.Combine(directory, "success.json"));
        var worker = new QueueProcessor(queue, journal, _ => Task.CompletedTask, _ => { prints++; return Task.CompletedTask; }, "Laser");
        worker.Tick().GetAwaiter().GetResult();
        Check(prints == 1 && queue.Events.SequenceEqual(new[] { "Spooling", "Submitted" }) && journal.Read() is null, "Normal order and durable completion");
        queue = new FakeQueue(payload) { FailAcknowledgment = true }; journal = new(Path.Combine(directory, "ack.json")); prints = 0;
        worker = new(queue, journal, _ => Task.CompletedTask, _ => { prints++; return Task.CompletedTask; }, "Laser");
        Failure(() => worker.Tick().GetAwaiter().GetResult());
        Check(journal.Read()?.Phase == "Submitted", "Ack failure retains submitted marker");
        queue.FailAcknowledgment = false;
        worker = new(queue, journal, _ => Task.CompletedTask, _ => { prints++; return Task.CompletedTask; }, "Laser");
        worker.Tick().GetAwaiter().GetResult(); Check(prints == 1 && journal.Read() is null, "Restart retries acknowledgment only");
        queue = new FakeQueue(payload); journal = new(Path.Combine(directory, "uncertain.json")); prints = 0;
        worker = new(queue, journal, _ => Task.CompletedTask, _ => { prints++; throw new IOException("Printer interrupted"); }, "Laser");
        Failure(() => worker.Tick().GetAwaiter().GetResult());
        worker = new(queue, journal, _ => Task.CompletedTask, _ => { prints++; return Task.CompletedTask; }, "Laser");
        Failure(() => worker.Tick().GetAwaiter().GetResult());
        Check(prints == 1 && queue.Events.Last() == "Uncertain", "Uncertain output is never automatically reprinted");
        queue.IsResolved = true; worker.Tick().GetAwaiter().GetResult(); Check(journal.Read() is null, "Owner resolution releases journal");
        queue = new FakeQueue(payload); journal = new(Path.Combine(directory, "preflight.json")); prints = 0;
        worker = new(queue, journal, _ => throw new ArgumentException("Invalid payload"), _ => { prints++; return Task.CompletedTask; }, "Laser");
        Failure(() => worker.Tick().GetAwaiter().GetResult()); Check(prints == 0 && queue.Events.Count == 0, "Preflight before side effects");
        queue = new FakeQueue(payload) { FailFence = true }; journal = new(Path.Combine(directory, "fence.json"));
        worker = new(queue, journal, _ => Task.CompletedTask, _ => { prints++; return Task.CompletedTask; }, "Laser");
        Failure(() => worker.Tick().GetAwaiter().GetResult()); Check(prints == 0, "Revocation/fence failure prevents printing");
        store.Forget(); Check(store.Load() is null, "Local credential removal");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class RecordingHandler(PrintConnection connection, PrintPayload payload) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Check(request.RequestUri!.AbsoluteUri == connection.Backend + "/rest/v1/rpc/print_device_poll", "Correct RPC endpoint");
            Check(request.Headers.GetValues("apikey").Single() == connection.ApiKey && request.Headers.Authorization is null, "Opaque public key is not a bearer token");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Check(body.RootElement.GetProperty("_secret").GetString() == connection.Secret && body.RootElement.GetProperty("_instance").GetGuid() != Guid.Empty && body.RootElement.GetProperty("_attempt").GetGuid() != Guid.Empty, "Scoped secret and idempotency identifiers");
            return new(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(new { id = payload.Id, item_id = payload.ItemId, code = payload.Code, description = payload.Description, height = payload.Height, phase = payload.Phase, state = payload.State }) };
        }
    }
    private static void Failure(Action action) { bool failed = false; try { action(); } catch (Exception) { failed = true; } Check(failed, "Expected failure"); }
    private sealed class FakeQueue(PrintPayload payload) : IPrintQueue
    {
        public bool FailAcknowledgment, FailFence, IsResolved;
        public List<string> Events = [];
        public Task<PrintPayload?> Claim(Guid instance, Guid attempt) => Task.FromResult<PrintPayload?>(payload);
        public Task<bool> Resolved(Guid instance, Guid attempt, Guid job) => Task.FromResult(IsResolved);
        public Task Event(Guid instance, Guid attempt, Guid job, string action, string printer)
        {
            Events.Add(action);
            if (action == "Submitted" && FailAcknowledgment || action == "Spooling" && FailFence) throw new IOException("Network interrupted");
            return Task.CompletedTask;
        }
    }
}
