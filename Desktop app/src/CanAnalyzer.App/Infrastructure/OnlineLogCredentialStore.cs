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
    private readonly string? _legacyPath;
    private readonly TimeProvider _timeProvider;

    public OnlineLogCredentialStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanAnalyzer", "online-login.dat"),
        legacyPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CanAnalyzer", "online-login.dat")) { }

    public OnlineLogCredentialStore(string path, TimeProvider? timeProvider = null, string? legacyPath = null)
    {
        _path = Path.GetFullPath(path);
        var fullLegacyPath = legacyPath is null ? null : Path.GetFullPath(legacyPath);
        _legacyPath = string.Equals(_path, fullLegacyPath, StringComparison.OrdinalIgnoreCase) ? null : fullLegacyPath;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RememberedOnlineLogCredentials? Load()
    {
        byte[]? plaintext = null;
        try
        {
            // An existing new file is authoritative, even when invalid; never revive
            // an older account after an expiry, rejected login or explicit sign-out.
            var sourcePath = File.Exists(_path) ? _path : _legacyPath;
            if (sourcePath is null || !File.Exists(sourcePath)) return null;
            if (new FileInfo(sourcePath).Length > 128 * 1024) throw new InvalidDataException();
            var ciphertext = File.ReadAllBytes(sourcePath);
            plaintext = ProtectedData.Unprotect(ciphertext, Purpose, DataProtectionScope.CurrentUser);
            var credentials = JsonSerializer.Deserialize<RememberedOnlineLogCredentials>(plaintext);
            if (credentials?.IsValidAt(_timeProvider.GetUtcNow(), OnlineLogService.DashboardBaseUrl) == true)
            {
                if (sourcePath == _legacyPath) TryMigrateLegacy(ciphertext);
                return credentials;
            }
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
        // Preserve the authoritative new file if an old copy cannot be removed;
        // otherwise a later Load could revive that stale legacy account.
        foreach (var path in new[] { _legacyPath, _path })
        {
            if (path is null) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new OnlineLogCredentialStorageException(
                    "De opgeslagen inloggegevens konden niet worden verwijderd. Probeer Afmelden opnieuw voordat je CANalyser afsluit.");
            }
        }
    }

    private void TryMigrateLegacy(byte[] ciphertext)
    {
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Copy the protected bytes unchanged: migration never extends the expiry
            // or enables remembering an account that was not already saved.
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temporaryPath, ciphertext);
            File.Move(temporaryPath, _path, overwrite: false);
            File.Delete(_legacyPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A failed move must not discard the original valid saved login.
            // Loading it still works; the next start can retry migration.
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
