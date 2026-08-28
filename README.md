# Bugwatch.Sdk (.NET / C#)

Official .NET SDK for [Bugwatch](https://bugwatch.roomylabs.com) error tracking. Zero-dependency (net8.0).

## Install

```bash
dotnet add package Bugwatch.Sdk
```

Or reference the built DLL: `src/bin/Release/net8.0/Bugwatch.Sdk.dll`

## Quickstart

```csharp
using Bugwatch;

// Global client
Bugwatch.Init("https://bw-YOUR_KEY@bugwatch-api.loadmindx.com/api/1", new BugwatchOptions
{
    Environment = "production",
    Release = "1.0.0",
});

Bugwatch.CaptureMessage("Payment confirmed", Level.Info);

try
{
    throw new InvalidOperationException("boom");
}
catch (Exception ex)
{
    Bugwatch.CaptureException(ex);  // captures full stack trace
}

// IMPORTANT: wait for async sends before process exit (short-lived apps)
Bugwatch.Flush();
```

Or use an explicit client (DI-friendly):

```csharp
using var client = new BugwatchClient(dsn, new BugwatchOptions { AllowHttp = false });
client.CaptureException(ex);
client.Flush();
```

## API

| Method | Description |
|---|---|
| `Init(dsn, options)` / `new BugwatchClient(dsn, options)` | Initialize with DSN + `Environment`, `Release`, `SampleRate`, `Debug`, `AllowHttp` |
| `CaptureMessage(msg, level)` | Capture a message; returns client-generated event id |
| `CaptureException(ex, level)` | Capture an exception with full stack trace |
| `SetTag(key, value)` | Attach a tag to all subsequent events |
| `SetUser(id, extra)` | Set user context (PII fields stripped) |
| `AddBreadcrumb(Breadcrumb)` | Add a breadcrumb (capped at 100) |
| `ClearScope()` | Clear tags, user, context, breadcrumbs |
| `Flush()` | **Required** — waits for pending async sends |
| `Dispose()` | Release the HttpClient |

## Behavior

- **HTTPS enforced** — `http://` DSNs are rejected unless `AllowHttp = true` (dev only)
- **PII stripped** — `email`, `ip_address`, `ip`, `username` never leave the host
- **Never fatal** — network failures are swallowed; your app keeps running
- **Async send** — events post on background tasks; call `Flush()` before exit
- **Stack traces** — full frame info (file, method, line) captured from `Exception`

## DSN format

```
https://{public_key}@{host}/api/{project_id}
```

The public key is sent as the `X-Bugwatch-Key` header. Find it in **Settings → API keys**.

## License

MIT — see [LICENSE](LICENSE).
