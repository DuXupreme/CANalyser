namespace CanAnalyzer.App.Services;

public interface IOnlineLogCredentialStore
{
    RememberedOnlineLogCredentials? Load();
    void Save(RememberedOnlineLogCredentials credentials);
    void Clear();
}

// Not a record: generated ToString output must never include authentication data.
public sealed class RememberedOnlineLogCredentials
{
    public const int MaximumDays = 30;
    public string DashboardUrl { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string AuthorizationParameter { get; init; } = string.Empty;
    public DateTimeOffset SavedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }

    public bool IsValidAt(DateTimeOffset now, string dashboardUrl)
    {
        if (DashboardUrl != dashboardUrl || string.IsNullOrWhiteSpace(UserName) || UserName.Contains(':') ||
            SavedAtUtc > now || ExpiresAtUtc <= now || ExpiresAtUtc <= SavedAtUtc ||
            ExpiresAtUtc - SavedAtUtc > TimeSpan.FromDays(MaximumDays)) return false;
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(AuthorizationParameter));
            return decoded.StartsWith(UserName + ":", StringComparison.Ordinal) && decoded.Length > UserName.Length + 1;
        }
        catch (ArgumentException) { return false; }
        catch (FormatException) { return false; }
    }
}

public sealed class OnlineLogCredentialStorageException(string message) : System.IO.IOException(message);
