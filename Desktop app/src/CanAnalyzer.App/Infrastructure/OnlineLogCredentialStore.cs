using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CanAnalyzer.App.Services;

namespace CanAnalyzer.App.Infrastructure;

/// <summary>Stores an expiring login protected by Windows DPAPI for the current user.</summary>
public sealed class OnlineLogCredentialStore : IOnlineLogCredentialStore
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("CANalyser.OnlineLogs.Login.v1");
    private readonly string _path;
    private readonly TimeProvider _timeProvider;

    public OnlineLogCredentialStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CanAnalyzer", "online-login.dat")) { }

    public OnlineLogCredentialStore(string path, TimeProvider? timeProvider = null)
    {
        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RememberedOnlineLogCredentials? Load()
    {
        byte[]? plaintext = null;
        try
        {
            if (!File.Exists(_path)) return null;
            if (new FileInfo(_path).Length > 128 * 1024) throw new InvalidDataException();
            plaintext = ProtectedData.Unprotect(File.ReadAllBytes(_path), Purpose, DataProtectionScope.CurrentUser);
            var credentials = JsonSerializer.Deserialize<RememberedOnlineLogCredentials>(plaintext);
            if (credentials?.IsValidAt(_timeProvider.GetUtcNow(), OnlineLogService.DashboardBaseUrl) == true)
                return credentials;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        {
            // An expired, unreadable or foreign Windows-user login never prevents signing in again.
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
        try { Clear(); }
        catch (OnlineLogCredentialStorageException) { }
        return null;
    }

    public void Save(RememberedOnlineLogCredentials credentials)
    {
        if (!credentials.IsValidAt(_timeProvider.GetUtcNow(), OnlineLogService.DashboardBaseUrl))
            throw new ArgumentException("De aanmelding is verlopen of ongeldig.", nameof(credentials));
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var ciphertext = ProtectedData.Protect(plaintext, Purpose, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temporaryPath, ciphertext);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new OnlineLogCredentialStorageException(
                "Je bent aangemeld, maar je inloggegevens konden niet worden onthouden. Probeer opnieuw; de aanmelding geldt nu alleen voor deze sessie.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OnlineLogCredentialStorageException(
                "De opgeslagen inloggegevens konden niet worden verwijderd. Probeer Afmelden opnieuw voordat je CANalyser afsluit.");
        }
    }
}
