using CanAnalyzer.App.Services;

namespace CanAnalyzer.App.Infrastructure;

public static class OnlineLogAuthenticationFlow
{
    public static async Task<T> RunAsync<T>(
        Func<Task<T>> operation,
        Func<bool, bool> requestCredentials,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation();
            }
            catch (OnlineLogAuthenticationException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var submitted = requestCredentials(exception.CredentialsRejected);
                cancellationToken.ThrowIfCancellationRequested();
                if (!submitted)
                    throw new OnlineLogSignInCanceledException();
            }
        }
    }
}

public sealed class OnlineLogSignInCanceledException : OperationCanceledException
{
    public OnlineLogSignInCanceledException() : base("Aanmelden geannuleerd.")
    {
    }
}
