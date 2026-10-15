using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomeClearout.PrintHelper;

public sealed record PrintConnection(int Version, string Backend, string ApiKey, string AppOrigin, string Secret)
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static PrintConnection Parse(string text)
    {
        var c = JsonSerializer.Deserialize<PrintConnection>(text, Json) ?? throw new ArgumentException("Paste a connection code from the app.");
        if (c.Version != 1 || string.IsNullOrWhiteSpace(c.ApiKey) || c.ApiKey.Length > 4096 || c.ApiKey.Any(char.IsControl) ||
            c.Secret is null || c.Secret.Length != 64 || c.Secret.Any(x => !"0123456789abcdef".Contains(x))) throw new ArgumentException("Invalid connection code.");
        if (!c.ApiKey.StartsWith("sb_publishable_", StringComparison.Ordinal))
        {
            try
            {
                var parts = c.ApiKey.Split('.');
                if (parts.Length != 3) throw new ArgumentException("Use the app’s public connection code.");
                var body = parts[1].Replace('-', '+').Replace('_', '/');
                body = body.PadRight((body.Length + 3) / 4 * 4, '=');
                using var claims = JsonDocument.Parse(Convert.FromBase64String(body));
                if (claims.RootElement.GetProperty("role").GetString() != "anon") throw new ArgumentException("Use a public anonymous key, never a service key.");
            }
            catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException) { throw new ArgumentException("Invalid public connection key."); }
        }
        foreach (var address in new[] { c.Backend, c.AppOrigin })
            if (!Uri.TryCreate(address, UriKind.Absolute, out var u) || u.Scheme != "https" || u.UserInfo != "" || u.Query != "" || u.Fragment != "" || u.AbsolutePath != "/") throw new ArgumentException("Connection requires HTTPS origins without paths or credentials.");
        return c;
    }
}

public sealed class ConnectionStore(string directory)
{
    private string FilePath => Path.Combine(directory, "connection.dpapi");
    public void Save(PrintConnection connection)
    {
        PrintConnection.Parse(JsonSerializer.Serialize(connection));
        Directory.CreateDirectory(directory);
        AtomicFile.Write(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(connection)), null, DataProtectionScope.CurrentUser));
    }
    public PrintConnection? Load() => File.Exists(FilePath) ? PrintConnection.Parse(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser))) : null;
    public void Forget() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}

public static class AtomicFile
{
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
        File.Move(temp, path, true);
    }
}

public sealed record PrintPayload(Guid Id, Guid ItemId, string Code, string Description, int Height, string Phase, string State)
{
    public LabelSpec Label(PrintConnection connection)
    {
        if (Id == Guid.Empty || ItemId == Guid.Empty) throw new ArgumentException("Invalid label identifiers.");
        var size = LabelSize.Presets.Single(s => s.HeightHundredths == Height);
        var text = string.IsNullOrWhiteSpace(Description) ? "Inventory item" : new string(Description.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        var characters = text.EnumerateRunes().Select(r => r.ToString()).ToArray();
        using var bitmap = new Bitmap(600, 600); using var graphics = Graphics.FromImage(bitmap);
        // Abbreviate only the printed description, never the permanent ID or stored title.
        for (int count = Math.Min(characters.Length, 60); count > 0; count--)
        {
            text = string.Concat(characters.Take(count)) + (count < characters.Length ? "…" : "");
            if (text.Length > 100) continue;
            try { LabelRenderer.FittingFontSize(graphics, text, LabelRenderer.Layout(size).Description, 10, 7, FontStyle.Regular); break; }
            catch (ArgumentException) when (count > 1) { }
        }
        var label = new LabelSpec(size, Code, text, connection.AppOrigin.TrimEnd('/') + "/item/" + ItemId);
        label.Validate(); return label;
    }
}

public interface IPrintQueue
{
    Task<PrintPayload?> Claim(Guid instance, Guid attempt);
    Task Event(Guid instance, Guid attempt, Guid job, string action, string printer);
    Task<bool> Resolved(Guid instance, Guid attempt, Guid job);
}

public sealed class PrintQueueClient(PrintConnection connection, HttpClient http) : IPrintQueue
{
    private async Task<JsonElement> Call(Guid instance, Guid attempt, Guid? job, string action, string? printer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.Backend.TrimEnd('/') + "/rest/v1/rpc/print_device_poll");
        request.Headers.Add("apikey", connection.ApiKey);
        // Legacy anon JWT keys need Authorization; opaque publishable keys must not be used as bearer JWTs.
        if (connection.ApiKey.StartsWith("eyJ", StringComparison.Ordinal)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + connection.ApiKey);
        request.Content = JsonContent.Create(new { _secret = connection.Secret, _instance = instance, _attempt = attempt, _job = job, _event = action, _printer = printer });
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Printing server returned {(int)response.StatusCode}. Check designation, connection and database migrations in the app. No automatic reprint was attempted.");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    public async Task<PrintPayload?> Claim(Guid instance, Guid attempt)
    {
        var data = await Call(instance, attempt, null, "claim");
        if (data.ValueKind == JsonValueKind.Null) return null;
        return new(data.GetProperty("id").GetGuid(), data.GetProperty("item_id").GetGuid(), data.GetProperty("code").GetString()!, data.GetProperty("description").GetString()!, data.GetProperty("height").GetInt32(), data.GetProperty("phase").GetString()!, data.GetProperty("state").GetString()!);
    }
    public async Task Event(Guid instance, Guid attempt, Guid job, string action, string printer) => _ = await Call(instance, attempt, job, action, printer);
    public async Task<bool> Resolved(Guid instance, Guid attempt, Guid job)
    {
        var data = await Call(instance, attempt, job, "status");
        return data.GetProperty("state").GetString() is "Printed" or "Cancelled" or "Failed";
    }
}

public sealed record PendingPrint(Guid Instance, Guid Attempt, Guid? Job, string Phase);
public sealed class PrintJournal(string path)
{
    public PendingPrint? Read() => File.Exists(path) ? JsonSerializer.Deserialize<PendingPrint>(File.ReadAllText(path)) ?? throw new IOException("Unreadable print journal; do not delete it until output is reconciled.") : null;
    public void Save(PendingPrint value) => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(value));
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}

// No automatic retry crosses the physical side effect. Only the server acknowledgment is retryable.
public sealed class QueueProcessor(IPrintQueue queue, PrintJournal journal, Func<PrintPayload, Task> preflight, Func<PrintPayload, Task> print, string printer)
{
    private readonly Guid instance = Guid.NewGuid();
    public async Task<string> Tick()
    {
        var pending = journal.Read();
        if (pending is not null)
        {
            if (pending.Job is Guid existing)
            {
                if (await queue.Resolved(pending.Instance, pending.Attempt, existing)) { journal.Clear(); return "Previous output resolved. Ready for the next label."; }
                if (pending.Phase == "Submitted")
                {
                    await queue.Event(pending.Instance, pending.Attempt, existing, "Submitted", printer);
                    journal.Clear(); return "Windows accepted the label. Check and confirm its physical output in the app.";
                }
                await queue.Event(pending.Instance, pending.Attempt, existing, "Uncertain", printer);
                throw new InvalidOperationException("Previous output is uncertain. Pause processing, check the printer, then resolve this attempt on the app’s Labels page before resuming.");
            }
            // Reconcile an interrupted claim using its original idempotency key. Never spool it after a restart.
            var recovered = await queue.Claim(pending.Instance, pending.Attempt);
            if (recovered is null) { journal.Clear(); return "No outstanding claim. Ready."; }
            journal.Save(pending with { Job = recovered.Id, Phase = "Uncertain" });
            await queue.Event(pending.Instance, pending.Attempt, recovered.Id, "Uncertain", printer);
            throw new InvalidOperationException("An interrupted claim needs review on the app’s Labels page.");
        }
        var attempt = Guid.NewGuid();
        pending = new(instance, attempt, null, "Claiming"); journal.Save(pending);
        var job = await queue.Claim(instance, attempt);
        if (job is null) { journal.Clear(); return "Connected. Waiting for labels from the app."; }
        pending = pending with { Job = job.Id, Phase = "Claimed" }; journal.Save(pending);
        if (job.Phase != "Claimed") throw new InvalidOperationException("Existing print attempt requires review; it will not be printed again.");
        await preflight(job);
        // Durable marker precedes both the server fence and printer call. Any failure now is uncertain.
        pending = pending with { Phase = "Spooling" }; journal.Save(pending);
        await queue.Event(instance, attempt, job.Id, "Spooling", printer);
        await print(job);
        pending = pending with { Phase = "Submitted" }; journal.Save(pending);
        await queue.Event(instance, attempt, job.Id, "Submitted", printer);
        journal.Clear();
        return $"{job.Code} accepted by Windows. Check and confirm physical output in the app.";
    }
}
