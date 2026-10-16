using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

// Proxy that makes OpenRouter look like an Ollama endpoint to GitHub Copilot for Agents.
//
// Usage: OpenrouterRouter [--config <path>]   (default: config.json next to the executable)
// All settings live in the config file; apiKey falls back to OPENROUTER_API_KEY if empty.

Options options;
try { options = Options.Load(args); }
catch (Exception ex) when (ex is ArgumentException or IOException or JsonException)
{
    Log.Write("ERROR", ex.Message, ConsoleColor.Red);
    return 1;
}
if (string.IsNullOrEmpty(options.ApiKey))
{
    Log.Write("ERROR", "No OpenRouter API key. Set \"apiKey\" in the config file or OPENROUTER_API_KEY", ConsoleColor.Red);
    return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(options.RequestTimeoutMinutes) };
var proxy = new Proxy(options, http);

var prefix = $"http://localhost:{options.Port}/";
using var listener = new HttpListener();
listener.Prefixes.Add(prefix);
try
{
    listener.Start();
}
catch (Exception ex)
{
    Log.Write("ERROR", $"Cannot bind to {prefix} - {ex.Message}", ConsoleColor.Red);
    Log.Write("WARN", "Try running as Administrator or change \"port\" in the config", ConsoleColor.Yellow);
    return 1;
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    try { listener.Stop(); } catch { }
};

Console.WriteLine();
Log.Banner("GitHub Copilot  ->  OpenRouter  (Ollama-shape proxy)");
Log.Line($"  Listening  : {prefix}", ConsoleColor.Cyan);
Log.Line($"  OpenRouter : {options.Url}", ConsoleColor.Cyan);
if (options.Filter.Length > 0) Log.Line($"  Filter     : *{options.Filter}*", ConsoleColor.Cyan);
Console.WriteLine();
Log.Line("  VS setup: Tools > Options > GitHub Copilot > Ollama endpoint", ConsoleColor.Yellow);
Log.Line($"            http://localhost:{options.Port}", ConsoleColor.White);
Console.WriteLine();
Log.Line("  Press Ctrl+C to stop.", ConsoleColor.DarkGray);
Console.WriteLine();

var inflight = new List<Task>();
while (listener.IsListening)
{
    HttpListenerContext ctx;
    try
    {
        ctx = await listener.GetContextAsync();
    }
    catch (HttpListenerException)
    {
        if (!listener.IsListening) break;
        continue;
    }
    catch (ObjectDisposedException)
    {
        break;
    }

    inflight.RemoveAll(t => t.IsCompleted);
    inflight.Add(Task.Run(() => proxy.HandleRequestAsync(ctx)));
}

try { await Task.WhenAll(inflight).WaitAsync(TimeSpan.FromSeconds(options.ShutdownGraceSeconds)); } catch { }
Log.Write("INFO", "Proxy stopped.");
return 0;

// ---------------------------------------------------------------------------

sealed class Options
{
    public int Port { get; set; } = 11434;
    public string ApiKey { get; set; } = "";
    public string Url { get; set; } = "https://openrouter.ai/api/v1";
    public string Filter { get; set; } = "";
    public bool Debug { get; set; }
    public string DefaultModel { get; set; } = "openai/gpt-4o";
    public string OllamaVersion { get; set; } = "0.3.0";
    public int RequestTimeoutMinutes { get; set; } = 15;
    public int UpstreamHeaderTimeoutSeconds { get; set; } = 30;
    public int ModelCacheMinutes { get; set; } = 5;
    public int ShutdownGraceSeconds { get; set; } = 5;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>The only command-line argument is "--config &lt;path&gt;"; everything else lives in the file.</summary>
    public static Options Load(string[] args)
    {
        string? path = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) path = args[++i];
            else throw new ArgumentException($"Unknown argument: {args[i]}. Usage: OpenrouterRouter [--config <path>]");
        }

        path ??= new[] { Path.Combine(AppContext.BaseDirectory, "config.json"), Path.Combine(Directory.GetCurrentDirectory(), "config.json") }
            .FirstOrDefault(File.Exists);
        if (path is null || !File.Exists(path))
            throw new FileNotFoundException("Configuration file not found. Create config.json next to the executable or pass --config <path>.");

        var o = JsonSerializer.Deserialize<Options>(File.ReadAllText(path), JsonOpts) ?? new Options();
        if (string.IsNullOrEmpty(o.ApiKey)) o.ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
        o.Url = o.Url.TrimEnd('/');
        Log.Write("INFO", $"Loaded configuration from {path}");
        return o;
    }
}

static class Log
{
    static readonly object Gate = new();

    public static void Write(string level, string msg, ConsoleColor? color = null)
    {
        var c = color ?? level switch
        {
            "INFO" => ConsoleColor.Cyan,
            "WARN" => ConsoleColor.Yellow,
            "ERROR" => ConsoleColor.Red,
            "DEBUG" => ConsoleColor.DarkGray,
            _ => ConsoleColor.White,
        };
        Line($"[{DateTime.Now:HH:mm:ss.fff}][{level}] {msg}", c);
    }

    public static void Line(string text, ConsoleColor color)
    {
        lock (Gate)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }

    public static void Banner(string title)
    {
        var line = new string('=', 59);
        Line(line, ConsoleColor.Green);
        Line($"  {title}", ConsoleColor.Green);
        Line(line, ConsoleColor.Green);
    }
}

sealed partial class Proxy(Options opt, HttpClient http)
{
    TimeSpan HeaderTimeout => TimeSpan.FromSeconds(opt.UpstreamHeaderTimeoutSeconds);

    // Model cache (guarded by _cacheLock)
    readonly SemaphoreSlim _cacheLock = new(1, 1);
    string _ollamaTags = """{"models":[]}""";
    string _openAiModels = """{"object":"list","data":[]}""";
    DateTime _expiry = DateTime.MinValue;

    [GeneratedRegex("\"model\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex ModelRegex();

    // ---------------------------------------------------------------- dispatch

    public async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            var method = req.HttpMethod;
            var path = req.Url!.AbsolutePath.TrimEnd('/');

            if (path != "/api/show") Log.Write("INFO", $"{method} {path}");
            if (opt.Debug)
            {
                var hdrs = string.Join(" | ", req.Headers.AllKeys.Select(k => $"{k}={req.Headers[k]}"));
                Log.Write("DEBUG", $"Headers: {hdrs}");
            }

            if (method == "OPTIONS") { await SendJsonAsync(resp, 204, ""); return; }
            if (method == "HEAD")
            {
                resp.StatusCode = 200;
                resp.Headers.Add("Access-Control-Allow-Origin", "*");
                resp.Close();
                return;
            }

            switch (path)
            {
                case "/api/version": await SendJsonAsync(resp, 200, new JsonObject { ["version"] = opt.OllamaVersion }.ToJsonString()); break;
                case "/api/tags": await RefreshModelsAsync(); await SendJsonAsync(resp, 200, _ollamaTags); break;
                case "/api/ps": await SendJsonAsync(resp, 200, """{"models":[]}"""); break;
                case "/api/show": await HandleShowAsync(req, resp); break;
                case "/v1/models": await RefreshModelsAsync(); await SendJsonAsync(resp, 200, _openAiModels); break;
                case "/api/chat":
                case "/v1/chat/completions": await HandleChatAsync(req, resp); break;
                default:
                    Log.Write("WARN", $"UNHANDLED {method} {path}");
                    await SendErrAsync(resp, 404, $"Unknown: {path}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("ERROR", $"Request failed: {ex.Message}");
            try { resp.Abort(); } catch { }
        }
    }

    // ---------------------------------------------------------------- helpers

    static async Task SendJsonAsync(HttpListenerResponse r, int code, string body)
    {
        try
        {
            r.StatusCode = code;
            r.ContentType = "application/json; charset=utf-8";
            AddCors(r);
            r.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
            r.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            var bytes = Encoding.UTF8.GetBytes(body);
            r.ContentLength64 = bytes.Length;
            await r.OutputStream.WriteAsync(bytes);
        }
        finally
        {
            try { r.OutputStream.Close(); } catch { }
        }
    }

    static void AddCors(HttpListenerResponse r) => r.Headers.Add("Access-Control-Allow-Origin", "*");

    static Task SendErrAsync(HttpListenerResponse r, int code, string msg)
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject { ["message"] = msg, ["code"] = code },
        };
        return SendJsonAsync(r, code, body.ToJsonString());
    }

    static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    HttpRequestMessage BuildUpstream(string path, string body)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, $"{opt.Url}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opt.ApiKey}");
        msg.Headers.TryAddWithoutValidation("HTTP-Referer", $"http://localhost:{opt.Port}");
        msg.Headers.TryAddWithoutValidation("X-Title", "VS-Copilot-OpenRouter-Proxy");
        return msg;
    }

    // ---------------------------------------------------------------- models

    async Task RefreshModelsAsync()
    {
        if (DateTime.Now < _expiry) return;

        await _cacheLock.WaitAsync();
        try
        {
            if (DateTime.Now < _expiry) return; // double-check

            Log.Write("INFO", "Fetching tool-calling models from OpenRouter...");
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{opt.Url}/models");
            msg.Headers.TryAddWithoutValidation("Authorization", $"Bearer {opt.ApiKey}");
            using var resp = await http.SendAsync(msg);
            resp.EnsureSuccessStatusCode();
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync());

            var ollama = new JsonArray();
            var openai = new JsonArray();
            var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ");
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var m in root?["data"]?.AsArray() ?? [])
            {
                if (m is null) continue;
                var hasTools = m["supported_parameters"] is JsonArray sp &&
                               sp.Any(p => p?.GetValue<string>() == "tools");
                if (!hasTools) continue;

                var id = m["id"]?.GetValue<string>() ?? "";
                if (opt.Filter.Length > 0 && !id.Contains(opt.Filter, StringComparison.OrdinalIgnoreCase)) continue;

                var family = id.Split('/')[0];
                var ctxLen = m["context_length"]?.ToString();
                var ctx = string.IsNullOrEmpty(ctxLen) || ctxLen == "0" ? "unknown" : $"{ctxLen}ctx";

                ollama.Add(new JsonObject
                {
                    ["name"] = id,
                    ["model"] = id,
                    ["modified_at"] = now,
                    ["size"] = 0,
                    ["size_vram"] = 0,
                    ["digest"] = "openrouter",
                    ["details"] = new JsonObject
                    {
                        ["parent_model"] = "",
                        ["format"] = "gguf",
                        ["family"] = family,
                        ["families"] = new JsonArray(family),
                        ["parameter_size"] = ctx,
                        ["quantization_level"] = "Q4_0",
                    },
                });
                openai.Add(new JsonObject
                {
                    ["id"] = id,
                    ["object"] = "model",
                    ["created"] = nowUnix,
                    ["owned_by"] = family,
                });
            }

            _ollamaTags = new JsonObject { ["models"] = ollama }.ToJsonString();
            _openAiModels = new JsonObject { ["object"] = "list", ["data"] = openai }.ToJsonString();
            _expiry = DateTime.Now.AddMinutes(opt.ModelCacheMinutes);

            Log.Write("INFO", $"Cached {ollama.Count} tool-calling models" +
                              (opt.Filter.Length > 0 ? $" (filter: *{opt.Filter}*)" : ""));
        }
        catch (Exception ex)
        {
            Log.Write("WARN", $"Failed to fetch models: {ex.Message}");
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    async Task HandleShowAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var showBody = await ReadBodyAsync(req);
        var m = ModelRegex().Match(showBody);
        var modelId = m.Success ? m.Groups[1].Value : "";
        var family = modelId.Split('/')[0];

        var show = new JsonObject
        {
            ["model"] = modelId,
            ["modified_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            ["modelfile"] = $"# Modelfile for {modelId}",
            ["parameters"] = "",
            ["template"] = "{{ .Prompt }}",
            ["details"] = new JsonObject
            {
                ["parent_model"] = "",
                ["format"] = "gguf",
                ["family"] = family,
                ["families"] = new JsonArray(family),
                ["parameter_size"] = "unknown",
                ["quantization_level"] = "Q4_0",
            },
            ["model_info"] = new JsonObject
            {
                ["general.architecture"] = family,
                ["general.basename"] = modelId,
                ["general.finetune"] = "",
                ["general.quantization_version"] = 2,
                ["general.size_label"] = "unknown",
            },
            ["capabilities"] = new JsonArray("completion", "tools"),
        };

        Log.Write("INFO", $"POST /api/show  {modelId}");
        await SendJsonAsync(resp, 200, show.ToJsonString());
    }

    // ---------------------------------------------------------------- chat

    async Task HandleChatAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        var body = await ReadBodyAsync(req);
        if (body.Length == 0) { await SendErrAsync(resp, 400, "Empty body"); return; }
        if (opt.Debug) Log.Write("DEBUG", $"REQ >> {body}");

        var model = opt.DefaultModel;
        var isStream = false;
        JsonObject? json = null;
        try { json = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { }

        if (json is not null)
        {
            if (json["model"] is JsonValue mv && mv.TryGetValue<string>(out var ms)) model = ms;
            if (json["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var sb)) isStream = sb;
        }

        Log.Write("INFO", $"--> OpenRouter  model={model}  stream={isStream}");

        var outBody = body;
        if (isStream && json is not null && json["stream_options"] is null)
        {
            json["stream_options"] = new JsonObject { ["include_usage"] = true };
            outBody = json.ToJsonString();
        }

        using var clientGone = new CancellationTokenSource();
        try
        {
            using var upstreamReq = BuildUpstream("/chat/completions", outBody);

            // 30-second timeout applies only until response headers arrive.
            using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(clientGone.Token);
            headerCts.CancelAfter(HeaderTimeout);

            HttpResponseMessage up;
            try
            {
                up = await http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);
            }
            catch (OperationCanceledException) when (!clientGone.IsCancellationRequested)
            {
                Log.Write("ERROR", $"Upstream timeout after {opt.UpstreamHeaderTimeoutSeconds} seconds");
                await SendErrAsync(resp, 504, $"Upstream did not respond within {opt.UpstreamHeaderTimeoutSeconds} seconds");
                return;
            }

            using (up)
            {
                if (!up.IsSuccessStatusCode)
                {
                    var err = await up.Content.ReadAsStringAsync();
                    Log.Write("ERROR", $"Upstream error: {up.StatusCode} - {err}");
                    await SendErrAsync(resp, (int)up.StatusCode, err);
                    return;
                }

                if (isStream) await PumpStreamAsync(up, resp, clientGone);
                else await PumpBodyAsync(up, resp);
            }
        }
        catch (IOException)
        {
            Log.Write("WARN", "Client closed connection mid-stream.");
            clientGone.Cancel();
        }
        catch (HttpListenerException)
        {
            Log.Write("WARN", "Client closed connection mid-stream.");
            clientGone.Cancel();
        }
        catch (OperationCanceledException)
        {
            Log.Write("ERROR", "Upstream timeout or cancelled");
        }
        catch (Exception ex)
        {
            Log.Write("ERROR", $"{(isStream ? "Stream" : "OpenRouter")} error: {ex.Message}");
            if (!isStream)
            {
                try { await SendErrAsync(resp, 502, ex.Message); } catch { }
            }
        }
        finally
        {
            try { resp.OutputStream.Close(); } catch { }
        }
    }

    async Task PumpStreamAsync(HttpResponseMessage up, HttpListenerResponse resp, CancellationTokenSource clientGone)
    {
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream; charset=utf-8";
        resp.Headers.Add("Cache-Control", "no-cache");
        AddCors(resp);
        resp.SendChunked = true;

        await using var upStream = await up.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(upStream, Encoding.UTF8);
        await using var writer = new StreamWriter(resp.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };

        string? promptTok = null, completionTok = null;
        while (await reader.ReadLineAsync(clientGone.Token) is { } line)
        {
            // A write failure (IOException / HttpListenerException) means the client hit
            // "stop generating"; the catch in HandleChatAsync disposes the upstream response.
            await writer.WriteLineAsync(line.AsMemory(), clientGone.Token);
            if (opt.Debug) Log.Write("DEBUG", $"<< {line}");

            if (line.Contains("\"prompt_tokens\"") || line.Contains("\"completion_tokens\""))
            {
                var p = PromptTokRegex().Match(line);
                var c = CompletionTokRegex().Match(line);
                if (p.Success) promptTok = p.Groups[1].Value;
                if (c.Success) completionTok = c.Groups[1].Value;
            }
        }

        if (promptTok is not null) Log.Write("INFO", $"<-- tokens  in={promptTok}  out={completionTok}");
    }

    static async Task PumpBodyAsync(HttpResponseMessage up, HttpListenerResponse resp)
    {
        var bytes = await up.Content.ReadAsByteArrayAsync();
        resp.StatusCode = 200;
        resp.ContentType = up.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
        AddCors(resp);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
    }

    [GeneratedRegex("\"prompt_tokens\"\\s*:\\s*(\\d+)")]
    private static partial Regex PromptTokRegex();

    [GeneratedRegex("\"completion_tokens\"\\s*:\\s*(\\d+)")]
    private static partial Regex CompletionTokRegex();
}

