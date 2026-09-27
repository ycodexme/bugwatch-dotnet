using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bugwatch;

public enum Level { Debug, Info, Warning, Error, Fatal }

public static class LevelExt
{
    public static string AsString(this Level l) => l switch
    {
        Level.Debug => "debug",
        Level.Info => "info",
        Level.Warning => "warning",
        Level.Error => "error",
        Level.Fatal => "fatal",
        _ => "error",
    };
}

public sealed class BugwatchOptions
{
    public string? Environment { get; set; }
    public string? Release { get; set; }
    public double SampleRate { get; set; } = 1.0;
    public bool Debug { get; set; }
    public bool AllowHttp { get; set; }
}

public sealed class Breadcrumb
{
    [JsonPropertyName("timestamp")] public long? Timestamp { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("level")] public string? Level { get; set; }
}

/// <summary>
/// Official Bugwatch SDK client for .NET (zero-dependency).
/// DSN: https://{public_key}@{host}/api/{project_id}
/// POST JSON to {base}/api/{project_id}/store/ with X-Bugwatch-Key.
/// </summary>
public sealed class BugwatchClient : IDisposable
{
    private const string Version = "0.1.0";
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(8);
    private const int MaxBreadcrumbs = 100;

    private static readonly string[] PiiUserKeys = { "email", "ip_address", "ip", "username" };

    private readonly string _storeUrl;
    private readonly string _publicKey;
    private readonly BugwatchOptions _options;
    private readonly object _scopeLock = new();
    private readonly Dictionary<string, string> _tags = new();
    private Dictionary<string, object?>? _user;
    private readonly Dictionary<string, object?> _context = new();
    private readonly List<Breadcrumb> _breadcrumbs = new();
    private readonly HttpClient _http;
    private static readonly Random _rng = new();

    public bool Initialized => true;

    public BugwatchClient(string dsn, BugwatchOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(dsn))
            throw new ArgumentException("DSN is required", nameof(dsn));
        var parsed = ParseDsn(dsn);
        _options = options ?? new BugwatchOptions();
        if (parsed.Scheme == "http" && !_options.AllowHttp)
            throw new ArgumentException(
                $"insecure DSN (http): {dsn} — pass AllowHttp=true for local dev", nameof(dsn));
        _storeUrl = parsed.StoreUrl;
        _publicKey = parsed.PublicKey;
        _http = new HttpClient { Timeout = NetworkTimeout };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Bugwatch-Key", _publicKey);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"bugwatch-dotnet/{Version}");
    }

    public sealed record ParsedDsn(string Scheme, string StoreUrl, string PublicKey);

    public static ParsedDsn ParseDsn(string dsn)
    {
        var idx = dsn.IndexOf("://", StringComparison.Ordinal);
        if (idx <= 0) throw new ArgumentException($"invalid DSN: {dsn}");
        var scheme = dsn[..idx];
        if (scheme is not ("https" or "http"))
            throw new ArgumentException($"invalid DSN scheme: {dsn}");
        var rest = dsn[(idx + 3)..];
        var at = rest.IndexOf('@');
        if (at <= 0) throw new ArgumentException($"invalid DSN (missing key): {dsn}");
        var key = rest[..at];
        var hostPath = rest[(at + 1)..];
        var lastSlash = hostPath.LastIndexOf('/');
        if (lastSlash < 0) throw new ArgumentException($"invalid DSN (missing project id): {dsn}");
        var projectId = hostPath[(lastSlash + 1)..];
        if (!long.TryParse(projectId, out _))
            throw new ArgumentException($"invalid project id in DSN: {dsn}");
        return new ParsedDsn(scheme, $"{scheme}://{hostPath}/store/", key);
    }

    // ---------- scope ----------

    public void SetTag(string key, string value)
    {
        lock (_scopeLock) { _tags[key] = value; }
    }

    public void SetUser(string id, Dictionary<string, object?>? extra = null)
    {
        var user = new Dictionary<string, object?> { ["id"] = id };
        if (extra is not null)
        {
            foreach (var (k, v) in extra)
            {
                if (PiiUserKeys.Contains(k)) continue; // PII strip
                user[k] = v;
            }
        }
        lock (_scopeLock) { _user = user; }
    }

    public void AddBreadcrumb(Breadcrumb crumb)
    {
        crumb.Timestamp ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_scopeLock)
        {
            _breadcrumbs.Add(crumb);
            if (_breadcrumbs.Count > MaxBreadcrumbs)
                _breadcrumbs.RemoveRange(0, _breadcrumbs.Count - MaxBreadcrumbs);
        }
    }

    public void ClearScope()
    {
        lock (_scopeLock)
        {
            _tags.Clear(); _user = null; _context.Clear(); _breadcrumbs.Clear();
        }
    }

    // ---------- capture ----------

    public string? CaptureMessage(string message, Level level = Level.Error)
    {
        return CaptureEvent(message, level, excType: null, excValue: null);
    }

    public string? CaptureException(Exception ex, Level level = Level.Error)
    {
        var frames = new List<object>();
        var st = new System.Diagnostics.StackTrace(ex, fNeedFileInfo: true);
        foreach (var frame in st.GetFrames().Reverse())
        {
            var method = frame.GetMethod();
            frames.Add(new
            {
                filename = frame.GetFileName() ?? "<unknown>",
                function = method is null ? "<unknown>" : $"{method.DeclaringType?.Name}.{method.Name}",
                lineno = frame.GetFileLineNumber() > 0 ? frame.GetFileLineNumber() : (int?)null,
            });
        }
        var payloadId = CaptureEvent(
            $"{ex.GetType().Name}: {ex.Message}",
            level,
            excType: ex.GetType().Name,
            excValue: ex.Message,
            frames: frames);
        return payloadId;
    }

    private string? CaptureEvent(
        string message, Level level, string? excType, string? excValue,
        List<object>? frames = null)
    {
        if (_rng.NextDouble() > _options.SampleRate) return null;

        var id = Guid.NewGuid().ToString("N"); // 32 hex, matches backend uuid hex storage
        var payload = new Dictionary<string, object?>
        {
            ["event_id"] = id,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["platform"] = "csharp",
            ["level"] = level.AsString(),
            ["message"] = message,
            ["sdk"] = new { name = "bugwatch-dotnet", version = Version },
        };
        if (excType is not null)
        {
            payload["exception"] = new Dictionary<string, object?>
            {
                ["type"] = excType,
                ["value"] = excValue ?? "",
                ["stacktrace"] = new { frames = frames ?? new List<object>() },
            };
        }
        if (!string.IsNullOrEmpty(_options.Environment)) payload["environment"] = _options.Environment;
        if (!string.IsNullOrEmpty(_options.Release)) payload["release"] = _options.Release;

        lock (_scopeLock)
        {
            if (_tags.Count > 0) payload["tags"] = new Dictionary<string, string>(_tags);
            if (_user is not null) payload["user"] = new Dictionary<string, object?>(_user);
            if (_breadcrumbs.Count > 0)
                payload["breadcrumbs"] = _breadcrumbs.Select(b => (object)b).ToList();
        }

        var json = JsonSerializer.Serialize(payload);
        var body = new StringContent(json, Encoding.UTF8, "application/json");
        // fire-and-forget: never block or crash the host app
        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await _http.PostAsync(_storeUrl, body);
                if (_options.Debug && !resp.IsSuccessStatusCode)
                    Console.Error.WriteLine($"[bugwatch] send failed: {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                if (_options.Debug) Console.Error.WriteLine($"[bugwatch] send error: {ex.Message}");
            }
        });
        return id;
    }

    /// <summary>Best-effort wait for pending async sends (short-lived processes).</summary>
    public void Flush() => Thread.Sleep(300);

    public void Dispose() => _http.Dispose();
}

public static class Bugwatch
{
    private static BugwatchClient? _global;
    private static readonly object InitLock = new();

    public static void Init(string dsn, BugwatchOptions? options = null)
    {
        lock (InitLock)
        {
            _global ??= new BugwatchClient(dsn, options);
        }
    }

    public static string? CaptureMessage(string message, Level level = Level.Error)
        => _global?.CaptureMessage(message, level);

    public static string? CaptureException(Exception ex, Level level = Level.Error)
        => _global?.CaptureException(ex, level);

    public static void SetTag(string key, string value) => _global?.SetTag(key, value);
    public static void SetUser(string id, Dictionary<string, object?>? extra = null) => _global?.SetUser(id, extra);
    public static void AddBreadcrumb(Breadcrumb crumb) => _global?.AddBreadcrumb(crumb);
    public static void ClearScope() => _global?.ClearScope();
    public static void Flush() => _global?.Flush();
}
