using TeamsEchoBot.Models;
using Microsoft.Graph.Communications.Client.Authentication;
using Microsoft.Identity.Client;
using System.Net.Http.Headers;

namespace TeamsEchoBot.Services;

public class AuthenticationProvider(BotConfiguration config, ILogger logger) : IRequestAuthenticationProvider
{
    private readonly BotConfiguration _config = config;
    private readonly ILogger _logger = logger;
    private IConfidentialClientApplication? _confidentialClient;

    private string? _cachedToken;
    private DateTimeOffset _tokenExpiry = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public async Task AuthenticateOutboundRequestAsync(HttpRequestMessage request, string tenant)
    {
        var token = await AcquireTokenAsync().ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public Task<RequestValidationResult> ValidateInboundRequestAsync(HttpRequestMessage request)
    {
        return Task.FromResult(new RequestValidationResult { IsValid = true });
    }

    private async Task<string> AcquireTokenAsync()
    {
        if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
            return _cachedToken;

        await _tokenLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cachedToken != null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
                return _cachedToken;

            _confidentialClient ??= ConfidentialClientApplicationBuilder
                .Create(_config.AadAppId)
                .WithClientSecret(_config.AadAppSecret)
                .WithAuthority($"https://login.microsoftonline.com/{_config.AadTenantId}")
                .Build();

            var scopes = new[] { "https://graph.microsoft.com/.default" };
            var result = await _confidentialClient
                .AcquireTokenForClient(scopes)
                .ExecuteAsync()
                .ConfigureAwait(false);

            _cachedToken = result.AccessToken;
            _tokenExpiry = result.ExpiresOn;

            _logger.LogInformation("Graph API token acquired. Expires: {Expiry}", result.ExpiresOn);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
