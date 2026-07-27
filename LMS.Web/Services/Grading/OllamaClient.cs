using System.Text;
using System.Text.Json;

namespace LMS.Web.Services.Grading;

/// <summary>HTTP client for the local Ollama daemon (default http://localhost:11434).
/// All inference runs on the LMS server — no external service, no per-use cost (§AIG-05).</summary>
public class OllamaClient : IOllamaClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public OllamaClient(HttpClient http, IConfiguration config)
    {
        _http = http;
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _http.BaseAddress = new Uri(baseUrl);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var resp = await _http.GetAsync("/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { model = "nomic-embed-text", prompt = text }, Json);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/embeddings")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var resp = await _http.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var arr = doc.RootElement.GetProperty("embedding");
            var vec = new float[arr.GetArrayLength()];
            int i = 0;
            foreach (var v in arr.EnumerateArray()) vec[i++] = v.GetSingle();
            return vec;
        }
        catch (Exception ex) when (ex is not GradingUnavailableException)
        {
            throw new GradingUnavailableException("Embedding request to Ollama failed.", ex);
        }
    }

    public async Task<string> ChatJsonAsync(string model, string system, string user, TimeSpan timeout, CancellationToken ct = default)
    {
        var payload = new
        {
            model,
            stream = false,
            format = "json",
            options = new { temperature = 0 },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };
        var body = JsonSerializer.Serialize(payload, Json);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var resp = await _http.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        }
        catch (Exception ex) when (ex is not GradingUnavailableException)
        {
            throw new GradingUnavailableException("Chat request to Ollama failed or timed out.", ex);
        }
    }
}
