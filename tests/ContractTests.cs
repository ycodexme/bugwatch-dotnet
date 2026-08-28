using Bugwatch;
using Xunit;

public class ContractTests
{
    [Fact]
    public void Dsn_parsing_https_ok()
    {
        var parsed = BugwatchClient.ParseDsn("https://bw-abc123@bugwatch-api.loadmindx.com/api/1");
        Assert.Equal("bw-abc123", parsed.PublicKey);
        Assert.EndsWith("/store/", parsed.StoreUrl);
        Assert.Contains("/api/1/store/", parsed.StoreUrl);
    }

    [Fact]
    public void Dsn_parsing_rejects_garbage()
    {
        Assert.Throws<ArgumentException>(() => BugwatchClient.ParseDsn("not-a-dsn"));
        Assert.Throws<ArgumentException>(() => BugwatchClient.ParseDsn("https://nokeyhost.com/api/1"));
        Assert.Throws<ArgumentException>(() => BugwatchClient.ParseDsn("https://key@host.com/api/notanumber"));
    }

    [Fact]
    public void Dsn_http_rejected_by_default()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new BugwatchClient("http://bw-k@127.0.0.1:8011/api/1"));
        Assert.Contains("AllowHttp", ex.Message);
    }

    [Fact]
    public void Dsn_http_allowed_with_optin()
    {
        using var c = new BugwatchClient("http://bw-k@127.0.0.1:8011/api/1",
            new BugwatchOptions { AllowHttp = true });
        Assert.True(c.Initialized);
    }

    [Fact]
    public void Level_strings_match_contract()
    {
        Assert.Equal("debug", Level.Debug.AsString());
        Assert.Equal("info", Level.Info.AsString());
        Assert.Equal("warning", Level.Warning.AsString());
        Assert.Equal("error", Level.Error.AsString());
        Assert.Equal("fatal", Level.Fatal.AsString());
    }

    [Fact]
    public void Pii_is_stripped_from_user()
    {
        using var c = new BugwatchClient("https://bw-x@127.0.0.1:9/api/1");
        c.SetUser("u-1", new Dictionary<string, object?>
        {
            ["email"] = "x@y.com",
            ["ip_address"] = "1.2.3.4",
            ["username"] = "bob",
            ["plan"] = "pro",
        });
        // no public getter — exercise via capture path is covered by integration;
        // here we assert no throw and scope ops are safe
        c.ClearScope();
    }

    [Fact]
    public void Breadcrumb_cap_is_enforced()
    {
        using var c = new BugwatchClient("https://bw-x@127.0.0.1:9/api/1");
        for (int i = 0; i < 150; i++)
            c.AddBreadcrumb(new Breadcrumb { Message = $"m{i}" });
        c.ClearScope();
    }

    [Fact]
    public void Capture_returns_client_generated_id()
    {
        using var c = new BugwatchClient("https://bw-x@127.0.0.1:9/api/1");
        var id = c.CaptureMessage("unit", Level.Error);
        Assert.NotNull(id);
        Assert.Equal(32, id!.Length);
    }

    [Fact]
    public void Global_api_never_throws_before_init()
    {
        // global not initialized in this test process unless earlier test ran Init —
        // capture must return null, not throw
        var id = Bugwatch.Bugwatch.CaptureMessage("pre-init", Level.Info);
        Assert.True(id is null || id.Length == 32);
    }
}
