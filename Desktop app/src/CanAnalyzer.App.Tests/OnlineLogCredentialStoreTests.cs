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

    [Fact]
    public void LegacyLogin_MigratesProtectedBytesAndOriginalDeadlineOutsideInstallDirectory()
    {
        var legacy = Path.Combine(_directory, "installation", "online-login.dat");
        var destination = Path.Combine(_directory, "roaming", "online-login.dat");
        var credentials = Credentials();
        new OnlineLogCredentialStore(legacy, _clock).Save(credentials);
        var originalBytes = File.ReadAllBytes(legacy);
        _clock.Now = _clock.Now.AddDays(5);
        var migrated = new OnlineLogCredentialStore(destination, _clock, legacy);

        var restored = Assert.IsType<RememberedOnlineLogCredentials>(migrated.Load());

        Assert.Equal(credentials.SavedAtUtc, restored.SavedAtUtc);
        Assert.Equal(credentials.ExpiresAtUtc, restored.ExpiresAtUtc);
        Assert.Equal(credentials.AuthorizationParameter, restored.AuthorizationParameter);
        Assert.Equal(originalBytes, File.ReadAllBytes(destination));
        Assert.False(File.Exists(legacy));
        Directory.Delete(Path.GetDirectoryName(legacy)!);
        Assert.NotNull(migrated.Load());
        _clock.Now = credentials.ExpiresAtUtc;
        Assert.Null(migrated.Load());
    }

    [Fact]
    public void NewLoginWins_AndClearRemovesBothLocationsWithoutRevivingLegacyAccount()
    {
        var legacy = Path.Combine(_directory, "legacy-login.dat");
        new OnlineLogCredentialStore(legacy, _clock).Save(Credentials());
        _clock.Now = _clock.Now.AddDays(1);
        var current = Credentials();
        var store = new OnlineLogCredentialStore(FilePath, _clock, legacy);
        store.Save(current);

        Assert.Equal(current.SavedAtUtc, store.Load()!.SavedAtUtc);
        Assert.True(File.Exists(legacy));
        store.Clear();
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(legacy));
        Assert.Null(store.Load());
    }

    [Fact]
    public void InvalidNewLoginNeverFallsBackToValidLegacyLogin()
    {
        var legacy = Path.Combine(_directory, "legacy-login.dat");
        new OnlineLogCredentialStore(legacy, _clock).Save(Credentials());
        File.WriteAllBytes(FilePath, [1, 2, 3]);
        var store = new OnlineLogCredentialStore(FilePath, _clock, legacy);

        Assert.Null(store.Load());
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void InvalidNewLoginWithLockedLegacyCannotReviveLegacyOnNextLoad()
    {
        var legacy = Path.Combine(_directory, "legacy-login.dat");
        new OnlineLogCredentialStore(legacy, _clock).Save(Credentials());
        File.WriteAllBytes(FilePath, [1, 2, 3]);
        var store = new OnlineLogCredentialStore(FilePath, _clock, legacy);
        using (var held = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Null(store.Load());
            Assert.True(File.Exists(FilePath));
            Assert.Null(store.Load());
        }
        Assert.Null(store.Load());
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(legacy));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("expired")]
    [InlineData("wrong-origin")]
    public void InvalidLegacyLoginIsNotMigrated(string mode)
    {
        var legacy = Path.Combine(_directory, "legacy-login.dat");
        Directory.CreateDirectory(_directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(Credentials(mode == "wrong-origin" ? "https://other.example/" : Dashboard));
        var protectedBytes = mode == "corrupt" ? new byte[] { 1, 2, 3 } : ProtectedData.Protect(plaintext,
            Encoding.UTF8.GetBytes("CANalyser.OnlineLogs.Login.v1"), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(legacy, protectedBytes);
        if (mode == "expired") _clock.Now = _clock.Now.AddDays(30);

        Assert.Null(new OnlineLogCredentialStore(FilePath, _clock, legacy).Load());
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void FailedMigrationPreservesValidLegacyLoginForRetry()
    {
        var legacy = Path.Combine(_directory, "legacy-login.dat");
        new OnlineLogCredentialStore(legacy, _clock).Save(Credentials());
        var original = File.ReadAllBytes(legacy);
        var blockedDirectory = Path.Combine(_directory, "blocked");
        File.WriteAllText(blockedDirectory, "This file prevents creating the destination directory.");
        var destination = Path.Combine(blockedDirectory, "online-login.dat");

        Assert.NotNull(new OnlineLogCredentialStore(destination, _clock, legacy).Load());
        Assert.Equal(original, File.ReadAllBytes(legacy));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void SameLegacyPathWithDifferentCaseDoesNotDeleteItsOwnSavedLogin()
    {
        Store().Save(Credentials());
        Assert.NotNull(new OnlineLogCredentialStore(FilePath, _clock, FilePath.ToUpperInvariant()).Load());
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void ClearReportsLockedLegacyFileAndRemovesItOnRetry()
    {
        var legacy = Path.Combine(_directory, "legacy-login.dat");
        new OnlineLogCredentialStore(legacy, _clock).Save(Credentials());
        var store = new OnlineLogCredentialStore(FilePath, _clock, legacy);
        store.Save(Credentials());
        using (var held = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<OnlineLogCredentialStorageException>(() => store.Clear());
        Assert.True(File.Exists(FilePath));
        Assert.True(File.Exists(legacy));
        store.Clear();
        Assert.Null(store.Load());
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
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
