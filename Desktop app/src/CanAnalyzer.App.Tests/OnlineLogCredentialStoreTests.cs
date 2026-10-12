using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Services;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class OnlineLogCredentialStoreTests : IDisposable
{
    private const string Dashboard = "https://main.d2qydggp5q6c4q.amplifyapp.com/";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "canalyser-login-store-test-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private string FilePath => Path.Combine(_directory, "online-login.dat");
    private OnlineLogCredentialStore Store() => new(FilePath, _clock);

    [Fact]
    public void RememberedLogin_IsEncrypted_RestoresExactCredentials_AndClearRemovesIt()
    {
        var credentials = Credentials();
        Store().Save(credentials);
        var bytes = File.ReadAllBytes(FilePath);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(credentials.UserName, text);
        Assert.DoesNotContain(credentials.AuthorizationParameter, text);
        Assert.DoesNotContain("test-only:pässword", text);
        Assert.False(bytes.AsSpan().StartsWith("{"u8));

        var restored = Assert.IsType<RememberedOnlineLogCredentials>(Store().Load());
        Assert.Equal(credentials.UserName, restored.UserName);
        Assert.Equal(credentials.AuthorizationParameter, restored.AuthorizationParameter);
        Assert.Equal(credentials.ExpiresAtUtc, restored.ExpiresAtUtc);
        Assert.Single(Directory.GetFiles(_directory));
        Store().Clear();
        Assert.False(File.Exists(FilePath));
        Assert.Null(Store().Load());
    }

    [Fact]
    public void Deadline_IsAbsoluteAcrossRestarts_AndExpiresAtExactlyThirtyDays()
    {
        var signedInAt = _clock.Now;
        Store().Save(Credentials());
        _clock.Now = signedInAt.AddDays(30).AddTicks(-1);
        Assert.NotNull(Store().Load());
        _clock.Now = signedInAt.AddDays(30);
        Assert.Null(Store().Load());
        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("wrong-purpose")]
    [InlineData("wrong-origin")]
    [InlineData("over-thirty-days")]
    [InlineData("future")]
    public void InvalidProtectedData_IsDiscarded(string mode)
    {
        Directory.CreateDirectory(_directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(Credentials(
            mode == "wrong-origin" ? "https://other.example/" : Dashboard,
            mode == "over-thirty-days" ? 31 : 30,
            mode == "future" ? _clock.Now.AddDays(1) : _clock.Now));
        var bytes = mode == "corrupt" ? new byte[] { 1, 2, 3 } : ProtectedData.Protect(plaintext,
            Encoding.UTF8.GetBytes(mode == "wrong-purpose" ? "different-purpose" : "CANalyser.OnlineLogs.Login.v1"),
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(FilePath, bytes);
        Assert.Null(Store().Load());
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void LockedCredentialFile_ReportsFailedRemovalAndPreservesItForRetry()
    {
        Store().Save(Credentials());
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<OnlineLogCredentialStorageException>(() => Store().Clear());
        Assert.NotNull(Store().Load());
        Store().Clear();
        Assert.Null(Store().Load());
    }

    private RememberedOnlineLogCredentials Credentials(string dashboard = Dashboard, int days = 30, DateTimeOffset? savedAt = null) => new()
    {
        DashboardUrl = dashboard,
        UserName = "test-operator",
        AuthorizationParameter = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-operator:  test-only:pässword  ")),
        SavedAtUtc = savedAt ?? _clock.Now,
        ExpiresAtUtc = (savedAt ?? _clock.Now).AddDays(days)
    };

    public void Dispose()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.EnumerateFiles(_directory)) File.Delete(file);
        Directory.Delete(_directory);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
