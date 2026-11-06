using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using XboxAuthNet.Game;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Authenticators;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.Game.SessionStorages;
using XboxAuthNet.OAuth;
using XboxAuthNet.OAuth.CodeFlow;
using XboxAuthNet.OAuth.CodeFlow.Parameters;
using XboxAuthNet.XboxLive;

namespace ArysLauncher;

public sealed class MicrosoftAccountAuthentication
{
    private readonly LauncherStorage storage;
    private readonly HttpClient client;

    public static string ClientId => JELoginHandler.DefaultMicrosoftOAuthClientInfo.ClientId;

    public MicrosoftAccountAuthentication(LauncherStorage storage, HttpClient client)
    {
        this.storage = storage;
        this.client = client;
    }

    public Task<LauncherAccount> SignInAsync(IWebUI browser, Action<string> report, CancellationToken cancellationToken)
    {
        return AuthenticateAsync(new JEGameAccount(new InMemorySessionStorage()), browser, null, report, cancellationToken);
    }

    public Task<LauncherAccount> RefreshAsync(LauncherAccount account, AccountTokens tokens, Action<string> report,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(account.MicrosoftClientId, ClientId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            throw new InvalidOperationException(LauncherText.Get("MicrosoftCompatibilitySignInRequired"));
        }
        var sdkAccount = new JEGameAccount(new InMemorySessionStorage());
        MicrosoftOAuthSessionSource.Default.Set(sdkAccount.SessionStorage, new MicrosoftOAuthResponse
        {
            RawRefreshToken = tokens.RefreshToken
        });
        return AuthenticateAsync(sdkAccount, null, account.PlayerUuid, report, cancellationToken);
    }

    private async Task<LauncherAccount> AuthenticateAsync(JEGameAccount sdkAccount, IWebUI? browser, string? expectedUuid,
        Action<string> report, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(browser == null ? 3 : 15));
        using var cancellation = timeout.Token.Register(client.CancelPendingRequests);
        var handler = new JELoginHandlerBuilder()
            .WithHttpClient(client)
            .WithAccountManager(new InMemoryXboxGameAccountManager(JEGameAccount.FromSessionStorage))
            .Build();
        var authenticator = handler.CreateAuthenticator(sdkAccount, timeout.Token);
        authenticator.AddAuthenticatorWithoutValidator(new AuthenticationStage(report,
            browser == null ? LauncherText.Get("MicrosoftRefreshingSession") : LauncherText.Get("MicrosoftSignInOnWeb")));
        if (browser == null)
        {
            authenticator.AddForceMicrosoftOAuthForJE(oauth => oauth.Silent());
        }
        else
        {
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            authenticator.AddForceMicrosoftOAuthForJE(oauth => oauth.Interactive(builder => builder.WithWebUI(browser),
                new CodeFlowAuthorizationParameter
                {
                    State = state,
                    Prompt = MicrosoftOAuthPromptModes.SelectAccount
                }));
        }
        authenticator.AddAuthenticatorWithoutValidator(new AuthenticationStage(report, LauncherText.Get("MicrosoftVerifyingXbox")));
        authenticator.AddForceXboxAuthForJE(xbox => xbox.Basic());
        authenticator.AddAuthenticatorWithoutValidator(new AuthenticationStage(report, LauncherText.Get("MicrosoftVerifyingMinecraft")));
        authenticator.AddForceJEAuthenticator(game => game.WithGameOwnershipChecker(true).Build());
        try
        {
            var session = await authenticator.ExecuteForLauncherAsync();
            timeout.Token.ThrowIfCancellationRequested();
            if (!Guid.TryParse(session.UUID, out var playerUuid) || string.IsNullOrWhiteSpace(session.Username) ||
                string.IsNullOrWhiteSpace(session.AccessToken))
            {
                throw new InvalidDataException(LauncherText.Get("MinecraftSessionIncomplete"));
            }
            var uuid = playerUuid.ToString("N");
            if (expectedUuid != null && !string.Equals(expectedUuid, uuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(LauncherText.Get("MicrosoftRenewedProfileMismatch"));
            }
            var oauth = MicrosoftOAuthSessionSource.Default.Get(sdkAccount.SessionStorage);
            var gameToken = sdkAccount.Token;
            if (string.IsNullOrWhiteSpace(oauth?.RawRefreshToken) || gameToken == null || gameToken.ExpiresOn <= DateTime.UtcNow)
            {
                throw new InvalidDataException(LauncherText.Get("MicrosoftSessionInvalid"));
            }
            var existing = storage.Accounts.FirstOrDefault(account => account.Kind == AccountKind.Microsoft &&
                string.Equals(account.PlayerUuid, uuid, StringComparison.OrdinalIgnoreCase));
            var account = new LauncherAccount
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
                Kind = AccountKind.Microsoft,
                MicrosoftClientId = ClientId,
                PlayerName = session.Username,
                PlayerUuid = uuid,
                ExpiresAt = new DateTimeOffset(DateTime.SpecifyKind(gameToken.ExpiresOn, DateTimeKind.Utc))
            };
            timeout.Token.ThrowIfCancellationRequested();
            storage.SaveAccount(account, new AccountTokens
            {
                AccessToken = session.AccessToken,
                RefreshToken = oauth.RawRefreshToken,
                Xuid = session.Xuid ?? "",
                XboxUserToken = sdkAccount.XboxTokens?.UserToken?.Token ?? "",
                XboxUserTokenExpiresAt = DateTimeOffset.TryParse(sdkAccount.XboxTokens?.UserToken?.ExpireOn, out var expiresAt) ?
                    expiresAt : DateTimeOffset.MinValue
            }, selectAccount: browser != null);
            report(LauncherText.Get("MicrosoftSignedIn"));
            return account;
        }
        catch (AuthCodeException)
        {
            throw new InvalidOperationException(LauncherText.Get("MicrosoftConsentIncomplete"));
        }
        catch (MicrosoftOAuthException exception)
        {
            throw new InvalidOperationException(LauncherText.Get("MicrosoftHttpFailure", exception.StatusCode));
        }
        catch (XboxAuthException exception)
        {
            var reason = exception.Error switch
            {
                "2148916233" => LauncherText.Get("XboxProfileMissing"),
                "2148916235" => LauncherText.Get("XboxRegionUnsupported"),
                "2148916236" or "2148916237" => LauncherText.Get("XboxAgeVerificationRequired"),
                "2148916238" => LauncherText.Get("XboxFamilyPermissionRequired"),
                _ => LauncherText.Get("XboxHttpFailure", exception.StatusCode, exception.Error ?? LauncherText.Get("NotProvided"))
            };
            throw new InvalidOperationException(reason);
        }
        catch (JEAuthException exception)
        {
            var reason = exception.StatusCode switch
            {
                401 => LauncherText.Get("MinecraftSessionExpired"),
                403 => LauncherText.Get("MinecraftAuthorizationRejected"),
                404 => LauncherText.Get("MinecraftJavaProfileMissing"),
                _ => LauncherText.Get("MinecraftJavaLicenseUnverified")
            };
            throw new InvalidOperationException(reason);
        }
    }

    private sealed class AuthenticationStage : IAuthenticator
    {
        private readonly Action<string> report;
        private readonly string message;

        public AuthenticationStage(Action<string> report, string message)
        {
            this.report = report;
            this.message = message;
        }

        public ValueTask ExecuteAsync(AuthenticateContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            report(message);
            return ValueTask.CompletedTask;
        }
    }
}
