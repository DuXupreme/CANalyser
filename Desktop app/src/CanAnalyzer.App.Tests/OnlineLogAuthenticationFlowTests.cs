using System.Net;
using System.Net.Http;
using CanAnalyzer.App.Infrastructure;
using CanAnalyzer.App.Services;
using Xunit;

namespace CanAnalyzer.App.Tests;

public sealed class OnlineLogAuthenticationFlowTests
{
    [Fact]
    public async Task AnonymousSuccess_DoesNotRequestCredentials()
    {
        var prompts = 0;

        var result = await OnlineLogAuthenticationFlow.RunAsync(
            () => Task.FromResult("logs"),
            _ => { prompts++; return true; },
            CancellationToken.None);

        Assert.Equal("logs", result);
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task Challenge_RetriesWithSubmittedCredentials()
    {
        var attempts = 0;
        var credentialsSubmitted = false;
        var prompts = new List<bool>();

        var result = await OnlineLogAuthenticationFlow.RunAsync(
            () =>
            {
                attempts++;
                return credentialsSubmitted
                    ? Task.FromResult("logs")
                    : Task.FromException<string>(new OnlineLogAuthenticationException(false));
            },
            rejected =>
            {
                prompts.Add(rejected);
                credentialsSubmitted = true;
                return true;
            },
            CancellationToken.None);

        Assert.Equal("logs", result);
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { false }, prompts);
    }

    [Fact]
    public async Task RejectedCredentials_RequireAnotherExplicitSubmissionForEveryRetry()
    {
        var events = new List<string>();
        var submissions = 0;
        var attempts = 0;

        var result = await OnlineLogAuthenticationFlow.RunAsync(
            () =>
            {
                attempts++;
                Assert.True(attempts <= 4, "A rejected request must not retry automatically.");
                events.Add($"request:{submissions}");
                return submissions == 3
                    ? Task.FromResult("logs")
                    : Task.FromException<string>(new OnlineLogAuthenticationException(submissions > 0));
            },
            rejected =>
            {
                events.Add($"prompt:{rejected}");
                Assert.True(submissions < 3, "Each request must use the latest explicit credential submission.");
                submissions++;
                return true;
            },
            CancellationToken.None);

        Assert.Equal("logs", result);
        Assert.Equal(new[]
        {
            "request:0", "prompt:False", "request:1", "prompt:True",
            "request:2", "prompt:True", "request:3"
        }, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledSignIn_DoesNotRetry(bool credentialsRejected)
    {
        var attempts = 0;
        var prompts = new List<bool>();

        await Assert.ThrowsAsync<OnlineLogSignInCanceledException>(() => OnlineLogAuthenticationFlow.RunAsync(
            () =>
            {
                attempts++;
                Assert.Equal(1, attempts);
                return Task.FromException<string>(new OnlineLogAuthenticationException(credentialsRejected));
            },
            rejected => { prompts.Add(rejected); return false; },
            CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Equal(new[] { credentialsRejected }, prompts);
    }

    [Fact]
    public async Task AlreadyCanceled_DoesNotRequestOrPrompt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var attempts = 0;
        var prompts = 0;

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => OnlineLogAuthenticationFlow.RunAsync(
            () => { attempts++; return Task.FromResult("logs"); },
            _ => { prompts++; return true; },
            cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, attempts);
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task CanceledDuringRequest_DoesNotPromptAfterChallenge()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var prompts = 0;

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => OnlineLogAuthenticationFlow.RunAsync(
            () =>
            {
                attempts++;
                cancellation.Cancel();
                return Task.FromException<string>(new OnlineLogAuthenticationException(false));
            },
            _ => { prompts++; return true; },
            cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, attempts);
        Assert.Equal(0, prompts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledDuringPrompt_DoesNotRetry(bool submitted)
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var prompts = 0;

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => OnlineLogAuthenticationFlow.RunAsync(
            () =>
            {
                attempts++;
                return Task.FromException<string>(new OnlineLogAuthenticationException(false));
            },
            _ => { prompts++; cancellation.Cancel(); return submitted; },
            cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, attempts);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task OtherHttpErrors_PropagateWithoutPromptOrRetry()
    {
        var failure = new HttpRequestException("Server unavailable.", null, HttpStatusCode.ServiceUnavailable);
        var attempts = 0;
        var prompts = 0;

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => OnlineLogAuthenticationFlow.RunAsync(
            () => { attempts++; return Task.FromException<string>(failure); },
            _ => { prompts++; return true; },
            CancellationToken.None));

        Assert.Same(failure, exception);
        Assert.Equal(1, attempts);
        Assert.Equal(0, prompts);
    }
}
