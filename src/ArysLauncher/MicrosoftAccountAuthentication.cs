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
            throw new InvalidOperationException("此账号需要使用新的兼容登录方式授权。请在账号页重新进行 Microsoft 登录。");
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
            browser == null ? "正在刷新 Microsoft 会话" : "请在微软官方网页中登录并授权"));
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
        authenticator.AddAuthenticatorWithoutValidator(new AuthenticationStage(report, "正在验证 Xbox Live 与 XSTS 授权"));
        authenticator.AddForceXboxAuthForJE(xbox => xbox.Basic());
        authenticator.AddAuthenticatorWithoutValidator(new AuthenticationStage(report, "正在验证 Minecraft 角色与 Java 版授权"));
        authenticator.AddForceJEAuthenticator(game => game.WithGameOwnershipChecker(true).Build());
        try
        {
            var session = await authenticator.ExecuteForLauncherAsync();
            timeout.Token.ThrowIfCancellationRequested();
            if (!Guid.TryParse(session.UUID, out var playerUuid) || string.IsNullOrWhiteSpace(session.Username) ||
                string.IsNullOrWhiteSpace(session.AccessToken))
            {
                throw new InvalidDataException("Minecraft 返回的角色或会话信息不完整，请重新登录。");
            }
            var uuid = playerUuid.ToString("N");
            if (expectedUuid != null && !string.Equals(expectedUuid, uuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("续期后的角色与原账号不一致，请在账号页重新登录。");
            }
            var oauth = MicrosoftOAuthSessionSource.Default.Get(sdkAccount.SessionStorage);
            var gameToken = sdkAccount.Token;
            if (string.IsNullOrWhiteSpace(oauth?.RawRefreshToken) || gameToken == null || gameToken.ExpiresOn <= DateTime.UtcNow)
            {
                throw new InvalidDataException("认证服务未返回可保存的有效会话，请重新登录。");
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
                Xuid = session.Xuid ?? ""
            });
            report("Microsoft 账号已登录");
            return account;
        }
        catch (AuthCodeException)
        {
            throw new InvalidOperationException("Microsoft 未完成本次授权，请重新登录并允许 Minecraft 访问。");
        }
        catch (MicrosoftOAuthException exception)
        {
            throw new InvalidOperationException($"Microsoft 授权未完成（HTTP {exception.StatusCode}）。请重新登录；若仍失败，请检查账号状态、网络或代理。");
        }
        catch (XboxAuthException exception)
        {
            var reason = exception.Error switch
            {
                "2148916233" => "此账号尚未建立 Xbox 档案，请先到 Xbox 官网完成注册。",
                "2148916235" => "此账号所在地区暂不支持 Xbox Live。",
                "2148916236" or "2148916237" => "此账号需要先在 Xbox 官网完成年龄确认。",
                "2148916238" => "此账号需要监护人在 Microsoft 家庭中授权使用 Xbox Live。",
                _ => $"Xbox 授权未完成（HTTP {exception.StatusCode}，错误码 {exception.Error ?? "未提供"}）。请检查 Xbox 账号状态。"
            };
            throw new InvalidOperationException(reason);
        }
        catch (JEAuthException exception)
        {
            var reason = exception.StatusCode switch
            {
                401 => "Minecraft 会话已失效，请重新进行 Microsoft 登录。",
                403 => "Minecraft 服务拒绝了本次认证，请稍后重试；兼容授权方式可能受服务端策略限制。",
                404 => "未找到 Java 版角色，请先在 Minecraft 官网建立游戏角色。",
                _ => "Minecraft 角色或 Java 版授权验证未完成。请确认账号拥有 Java 版并已建立角色，然后重新登录。"
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
