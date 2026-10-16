# OpenrouterRouter

A small local proxy that makes [OpenRouter](https://openrouter.ai) look like an [Ollama](https://ollama.com) server. It lets GitHub Copilot in Visual Studio (via its **Ollama endpoint** setting) use any tool-calling model available on OpenRouter.

```
Visual Studio / Copilot  --(Ollama API)-->  OpenrouterRouter  --(OpenAI API)-->  OpenRouter
```

## How it works

- Listens on `http://localhost:11434` (Ollama's default port).
- Answers Ollama discovery endpoints (`/api/version`, `/api/tags`, `/api/show`, `/api/ps`) using OpenRouter's model list. Only models that advertise `tools` support are exposed, and every model reports the `completion` and `tools` capabilities.
- Forwards chat requests to OpenRouter's `/chat/completions`, streaming Server-Sent Events back unchanged. For streaming requests it adds `stream_options.include_usage` so token counts are logged.
- Stops the upstream request when the client cancels ("stop generating").
- Caches the model list in memory (5 minutes by default).

### Supported endpoints

| Method | Path | Behaviour |
| --- | --- | --- |
| GET | `/api/version` | Fake Ollama version (`ollamaVersion` setting) |
| GET | `/api/tags` | Tool-capable OpenRouter models, Ollama format |
| GET | `/api/ps` | Always an empty list |
| POST | `/api/show` | Synthetic model details for the requested model |
| GET | `/v1/models` | Tool-capable OpenRouter models, OpenAI format |
| POST | `/api/chat`, `/v1/chat/completions` | Proxied to OpenRouter `/chat/completions` |

`OPTIONS` and `HEAD` requests are answered locally, with permissive CORS headers. Any other path returns 404.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- An OpenRouter API key
- Windows for the optional event log mirroring. The proxy itself has no other Windows dependency.

## Build and run

The repo ships `OpenrouterRouter/config.json.sample`. Copy it to `config.json` in the same folder and edit it before running (see [Configuration](#configuration)).

```powershell
Copy-Item OpenrouterRouter\config.json.sample OpenrouterRouter\config.json
dotnet run --project OpenrouterRouter
```

To use a config file from another location:

```powershell
dotnet run --project OpenrouterRouter -- --config C:\path\to\config.json
```

If `--config` is omitted, `config.json` is looked up next to the executable, then in the current directory. `--config <path>` is the only command-line argument.

## Publish a single exe

```powershell
dotnet publish OpenrouterRouter -c Release -o publish
```

This produces `publish\OpenrouterRouter.exe`, a single self-contained `win-x64` executable (about 36 MB) that includes the .NET runtime, so the target machine needs nothing installed. `config.json.sample` is copied next to it. Rename it to `config.json` and edit it, or pass `--config <path>`:

```powershell
Copy-Item publish\config.json.sample publish\config.json
.\publish\OpenrouterRouter.exe
```

The publish settings live in `OpenrouterRouter.csproj`. Trimming is deliberately off because it can break the reflection-based config loading. To target another platform, change `RuntimeIdentifier` there (the event log feature is Windows-only).

## Configuration

All settings live in `config.json` (created from `config.json.sample`; the sample is copied to the output directory on build and publish). Comments and trailing commas are allowed, and unknown keys are rejected.

| Key | Default | Description |
| --- | --- | --- |
| `apiKey` | `""` | OpenRouter API key. If empty, the `OPENROUTER_API_KEY` environment variable is used. |
| `port` | `11434` | Local port to listen on. |
| `url` | `https://openrouter.ai/api/v1` | OpenRouter base URL. |
| `filter` | `""` | Case-insensitive substring filter on model IDs, e.g. `anthropic`. Empty exposes all tool-capable models. |
| `debug` | `false` | Log request headers and bodies and every SSE line. |
| `defaultModel` | `openai/gpt-4o` | Model used when a request doesn't name one. |
| `ollamaVersion` | `0.3.0` | Version string returned by `/api/version`. |
| `requestTimeoutMinutes` | `15` | Overall upstream HTTP timeout. |
| `upstreamHeaderTimeoutSeconds` | `30` | How long to wait for OpenRouter to start responding. Returns 504 on expiry. |
| `modelCacheMinutes` | `5` | How long the model list is cached. |
| `shutdownGraceSeconds` | `5` | How long in-flight requests get to finish on Ctrl+C. |
| `eventLog` | `false` (`config.json.sample` sets `true`) | Mirror DEBUG, INFO, WARN and ERROR log lines to the Windows Application event log. |

> **Keep your API key out of source control.** Prefer the `OPENROUTER_API_KEY` environment variable over writing the key into `config.json`.

## Visual Studio setup

1. Start the proxy.
2. In Visual Studio, open **Tools > Options > GitHub Copilot** and set the **Ollama endpoint** to `http://localhost:11434` (or your configured port).
3. Pick one of the OpenRouter models from the Copilot model picker.

## Windows event log

With `"eventLog": true`, DEBUG, INFO, WARN and ERROR messages are also written to the Application log under the source `OpenrouterRouter`. DEBUG entries are recorded as informational events and prefixed with `[DEBUG]`. Registering the event source requires administrator rights once, so run the proxy elevated the first time. If registration fails, the proxy logs a warning and carries on without the event log.

## Troubleshooting

- **"Cannot bind to http://localhost:11434/"**: the port is in use (is real Ollama running?) or you lack permission. Change `port` or run as Administrator.
- **"No OpenRouter API key"**: set `apiKey` in the config or the `OPENROUTER_API_KEY` environment variable.
- **Empty model list**: check the warning from the model fetch in the log, and make sure `filter` isn't excluding everything.
- **504 from the proxy**: OpenRouter didn't send response headers within `upstreamHeaderTimeoutSeconds`.

## Credits

This project is based on the original [openrouter-copilot-proxy](https://github.com/sander-/openrouter-copilot-proxy) by [sander-](https://github.com/sander-). Thanks for the idea and the original implementation.
