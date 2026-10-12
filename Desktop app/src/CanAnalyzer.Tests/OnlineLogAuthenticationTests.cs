using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Services;
using Xunit;

namespace CanAnalyzer.Tests;

public sealed class OnlineLogAuthenticationTests
{
    [Fact]
    public async Task AnonymousDashboardStillWorksWithoutCredentials()
    {
        using var service = CreateService(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return LogList();
        });

        Assert.Empty((await GetLogs(service)).Files);
        Assert.Null(service.AuthenticationUserName);
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("bAsIc")]
    public async Task BasicChallengeRequestsLoginWithoutExposingResponseBody(string scheme)
    {
        using var service = CreateService(_ => Challenge(scheme));

        var error = await Assert.ThrowsAsync<OnlineLogAuthenticationException>(() => GetLogs(service));

        Assert.False(error.CredentialsRejected);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain("unsafe-server-message", error.ToString());
    }

    [Fact]
    public async Task CallerCanReplaceRejectedCredentialsAndRetry()
    {
        var received = new List<string?>();
        using var service = CreateService(request =>
        {
            received.Add(request.Headers.Authorization?.ToString());
            return received.Count == 1 ? Challenge("Basic") : LogList();
        });
        service.SetCredentials("operator", "wrong-password");

        var error = await Assert.ThrowsAsync<OnlineLogAuthenticationException>(() => GetLogs(service));
        Assert.True(error.CredentialsRejected);
        Assert.DoesNotContain("wrong-password", error.ToString());
        Assert.Single(received); // The service does not automatically retry rejected credentials.

        service.SetCredentials("operator", " correct password ");
        Assert.Empty((await GetLogs(service)).Files);
        Assert.Equal("operator", service.AuthenticationUserName);
        Assert.Equal(new[] { Basic("operator", "wrong-password"), Basic("operator", " correct password ") }, received);
    }

    [Fact]
    public async Task ClearCredentialsMakesFollowingRequestsAnonymous()
    {
        using var service = CreateService(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return LogList();
        });
        service.SetCredentials("operator", "password");
        service.ClearCredentials();

        await GetLogs(service);

        Assert.Null(service.AuthenticationUserName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer")]
    [InlineData("Digest")]
    public async Task OtherUnauthorizedResponsesDoNotRequestBasicLogin(string? scheme)
    {
        using var service = CreateService(_ => Challenge(scheme));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => GetLogs(service));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Fact]
    public async Task ChallengeAtRedirectedExternalOriginDoesNotRequestDashboardLogin()
    {
        using var service = CreateService(request =>
        {
            // HttpClient handlers may update the original request URI while following redirects.
            request.RequestUri = new Uri("https://storage.example.test/redirected");
            var response = Challenge("Basic");
            response.RequestMessage = request;
            return response;
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => GetLogs(service));
    }

    [Fact]
    public async Task PlanUsesCredentialsButSignedDownloadDoesNot()
    {
        using var cache = new TemporaryCache();
        var requests = new List<string>();
        using var service = CreateService(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.Host == "dashboard.example.test")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(Basic("operator", " password "), request.Headers.Authorization?.ToString());
                return Plan();
            }

            Assert.Equal("storage.example.test", request.RequestUri.Host);
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([42]) };
        }, cache.Path);
        service.SetCredentials("operator", " password ");

        var archive = await service.DownloadArchiveAsync(Selection(), null, default);

        Assert.True(File.Exists(archive));
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task PlanBasicChallengeRequestsLoginAndRemovesPartialArchive()
    {
        using var cache = new TemporaryCache();
        using var service = CreateService(_ => Challenge("Basic"), cache.Path);
        service.SetCredentials("operator", "password");

        var error = await Assert.ThrowsAsync<OnlineLogAuthenticationException>(() =>
            service.DownloadArchiveAsync(Selection(), null, default));

        Assert.True(error.CredentialsRejected);
        Assert.Empty(Directory.GetFiles(cache.Path));
    }

    [Fact]
    public async Task SignedDownloadUnauthorizedDoesNotRequestDashboardLogin()
    {
        using var cache = new TemporaryCache();
        using var service = CreateService(request => request.Method == HttpMethod.Post ? Plan() : Challenge("Basic"), cache.Path);
        service.SetCredentials("operator", "password");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.DownloadArchiveAsync(Selection(), null, default));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Empty(Directory.GetFiles(cache.Path));
    }

    [Fact]
    public async Task CredentialsAreNeverSentOverHttp()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(_ => { requests++; return LogList(); }))
        { BaseAddress = new Uri("http://dashboard.example.test/") };
        using var service = new OnlineLogService(client);
        service.SetCredentials("operator", "password");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GetLogs(service));

        Assert.Contains("HTTPS", error.Message);
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData(" ", "password")]
    [InlineData("operator:name", "password")]
    [InlineData("operator", "")]
    public void InvalidCredentialsDoNotReplaceExistingCredentials(string username, string password)
    {
        using var service = CreateService(_ => LogList());
        service.SetCredentials("existing-operator", "existing-password");

        Assert.Throws<ArgumentException>(() => service.SetCredentials(username, password));

        Assert.Equal("existing-operator", service.AuthenticationUserName);
    }

    [Fact]
    public async Task CancelledRequestDoesNotClearCredentials()
    {
        using var service = CreateService(_ => LogList());
        service.SetCredentials("operator", "password");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetLogsAsync("22484AAA",
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, cancellation.Token));

        Assert.Equal("operator", service.AuthenticationUserName);
    }

    private static string Basic(string username, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));

    private static Task<OnlineLogQueryResult> GetLogs(OnlineLogService service) => service.GetLogsAsync("22484AAA",
        DateTimeOffset.Parse("2026-10-04T00:00:00Z"), DateTimeOffset.Parse("2026-10-05T00:00:00Z"), default);

    private static OnlineLogSelection[] Selection() =>
        [new("22484AAA/00000072/00000001.MF4", "00000001.MF4", "22484AAA", "00000072", 1)];

    private static HttpResponseMessage LogList() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { files = Array.Empty<object>(), timeBasis = "recording" })
    };

    private static HttpResponseMessage Plan() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            files = new[] { new { archiveName = "22484AAA/00000072/00000001.MF4", url = "https://storage.example.test/signed.mf4" } },
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        })
    };

    private static HttpResponseMessage Challenge(string? scheme)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = JsonContent.Create(new { error = "unsafe-server-message" }) };
        if (scheme is not null) response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(scheme, "realm=\"dashboard\""));
        return response;
    }

    private static OnlineLogService CreateService(Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? cache = null, IOnlineLogCredentialStore? store = null, TimeProvider? clock = null) => new(new HttpClient(new Handler(respond))
        { BaseAddress = new Uri("https://dashboard.example.test/") }, cache, store, clock);

    [Fact]
    public async Task RememberedLogin_SavesOnlyAfterAcceptance_AndSurvivesRestartWithoutExtendingExpiry()
    {
        var store = new MemoryCredentialStore();
        var clock = new TestClock();
        var signedInAt = clock.Now;
        using (var service = CreateService(_ => LogList(), store: store, clock: clock))
        {
            service.SetCredentials("operator", " test-only ", rememberFor30Days: true);
            Assert.Null(store.Saved);
            await GetLogs(service);
            Assert.Equal(signedInAt.AddDays(30), store.Saved!.ExpiresAtUtc);
            clock.Now = signedInAt.AddDays(10);
            await GetLogs(service);
            Assert.Equal(1, store.SaveCount);
        }
        Assert.NotNull(store.Saved); // Dispose must not delete remembered logins.
        using var restored = CreateService(request =>
        {
            Assert.Equal(Basic("operator", " test-only "), request.Headers.Authorization?.ToString());
            return LogList();
        }, store: store, clock: clock);
        Assert.True(restored.RememberCredentials);
        Assert.Equal("operator", restored.AuthenticationUserName);
        await GetLogs(restored);
        Assert.Equal(signedInAt.AddDays(30), store.Saved!.ExpiresAtUtc);
        restored.ClearCredentials();
        Assert.Null(store.Saved);
        Assert.Null(restored.AuthenticationUserName);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-1)]
    public async Task ExpiredOrClockRolledBack_RememberedLoginIsNotSent(int days)
    {
        var store = new MemoryCredentialStore();
        var clock = new TestClock();
        var signedInAt = clock.Now;
        var requests = 0;
        using var service = CreateService(request =>
        {
            if (++requests > 1) Assert.Null(request.Headers.Authorization);
            return LogList();
        }, store: store, clock: clock);
        service.SetCredentials("operator", "test-only", true);
        await GetLogs(service);
        clock.Now = signedInAt.AddDays(days);
        await GetLogs(service);
        Assert.Null(store.Saved);
        Assert.Null(service.AuthenticationUserName);
        Assert.False(service.RememberCredentials);
    }

    [Fact]
    public async Task SessionOnlyLogin_RemovesPreviouslyRememberedAccount()
    {
        var store = new MemoryCredentialStore();
        using var service = CreateService(_ => LogList(), store: store);
        service.SetCredentials("old", "test-only", true);
        await GetLogs(service);
        Assert.NotNull(store.Saved);
        service.SetCredentials("new", "session-only");
        Assert.Null(store.Saved);
        await GetLogs(service);
        Assert.Equal(1, store.SaveCount);
        Assert.False(service.RememberCredentials);
    }

    [Fact]
    public async Task RejectedRememberedLogin_IsRemovedAndNotSavedAgain()
    {
        var store = new MemoryCredentialStore();
        var reject = false;
        using var service = CreateService(_ => reject ? Challenge("Basic") : LogList(), store: store);
        service.SetCredentials("operator", "test-only", true);
        await GetLogs(service);
        reject = true;
        var error = await Assert.ThrowsAsync<OnlineLogAuthenticationException>(() => GetLogs(service));
        Assert.True(error.CredentialsRejected);
        Assert.Null(store.Saved);
        Assert.False(service.RememberCredentials);
        Assert.Equal("operator", service.AuthenticationUserName); // Prefill the correction dialog only.
        reject = false;
        await GetLogs(service);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task RememberFailure_IsReportedOnceAndFallsBackToSessionOnly()
    {
        var store = new MemoryCredentialStore { FailSave = true };
        using var service = CreateService(_ => LogList(), store: store);
        service.SetCredentials("operator", "test-only", true);
        await Assert.ThrowsAsync<OnlineLogCredentialStorageException>(() => GetLogs(service));
        Assert.False(service.RememberCredentials);
        Assert.Null(store.Saved);
        await GetLogs(service);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task SignOutFailure_DoesNotClaimCredentialsWereRemoved_AndCanBeRetried()
    {
        var store = new MemoryCredentialStore();
        using var service = CreateService(_ => LogList(), store: store);
        service.SetCredentials("operator", "test-only", true);
        await GetLogs(service);
        store.FailClear = true;
        Assert.Throws<OnlineLogCredentialStorageException>(service.ClearCredentials);
        Assert.True(service.RememberCredentials);
        store.FailClear = false;
        service.ClearCredentials();
        Assert.Null(service.AuthenticationUserName);
        Assert.Null(store.Saved);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryCredentialStore : IOnlineLogCredentialStore
    {
        public RememberedOnlineLogCredentials? Saved { get; private set; }
        public int SaveCount { get; private set; }
        public bool FailSave { get; init; }
        public bool FailClear { get; set; }
        public RememberedOnlineLogCredentials? Load() => Saved;
        public void Save(RememberedOnlineLogCredentials credentials)
        {
            SaveCount++;
            if (FailSave) throw new OnlineLogCredentialStorageException("Could not save test credentials.");
            Saved = credentials;
        }
        public void Clear()
        {
            if (FailClear) throw new OnlineLogCredentialStorageException("Could not delete test credentials.");
            Saved = null;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TemporaryCache : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "canalyser-auth-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
