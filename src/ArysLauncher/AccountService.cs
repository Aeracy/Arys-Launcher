using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using XboxAuthNet.OAuth.CodeFlow;

namespace ArysLauncher;

public sealed class AccountService : IDisposable
{
    private readonly LauncherStorage storage;
    private readonly HttpClient client;

    public AccountService(LauncherStorage storage)
    {
        this.storage = storage;
        client = NetworkClients.Create(storage.Settings, false);
        client.Timeout = TimeSpan.FromSeconds(45);
        client.MaxResponseContentBufferSize = 2 * 1024 * 1024;
    }

    public Task<LauncherAccount> LoginMicrosoftAsync(IWebUI browser, Action<string> report, CancellationToken cancellationToken)
    {
        return new MicrosoftAccountAuthentication(storage, client).SignInAsync(browser, report, cancellationToken);
    }

    public async Task<LauncherAccount> LoginExternalAsync(
        string serverAddress,
        string username,
        string password,
        string serverName,
        Func<IReadOnlyList<GameProfile>, Task<GameProfile>> chooseProfile,
        CancellationToken cancellationToken)
    {
        var server = NormalizeServer(serverAddress);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("请填写认证服务器的账号和密码。");
        }
        var clientToken = Guid.NewGuid().ToString("N");
        using var response = await client.PostAsJsonAsync(new Uri(server, "authserver/authenticate"), new
        {
            agent = new { name = "Minecraft", version = 1 },
            username,
            password,
            clientToken,
            requestUser = true
        }, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        EnsureSuccess(response, document.RootElement, "外置账号登录");
        var accessToken = RequiredString(document.RootElement, "accessToken");
        clientToken = OptionalString(document.RootElement, "clientToken", clientToken);
        var profiles = new List<GameProfile>();
        if (document.RootElement.TryGetProperty("availableProfiles", out var availableProfiles))
        {
            foreach (var profile in availableProfiles.EnumerateArray())
            {
                profiles.Add(ReadProfile(profile));
            }
        }
        GameProfile? selected = null;
        if (document.RootElement.TryGetProperty("selectedProfile", out var selectedElement) && selectedElement.ValueKind == JsonValueKind.Object)
        {
            selected = ReadProfile(selectedElement);
            if (profiles.All(profile => profile.Id != selected.Id))
            {
                profiles.Add(selected);
            }
        }
        if (profiles.Count == 0)
        {
            throw new InvalidOperationException("认证成功，但该账号没有 Minecraft 角色。请先在认证站创建角色。");
        }
        var chosen = profiles.Count == 1 ? profiles[0] : await chooseProfile(profiles);
        if (profiles.All(profile => profile.Id != chosen.Id))
        {
            throw new InvalidDataException("选择的角色不属于此账号。");
        }
        var account = new LauncherAccount
        {
            Kind = AccountKind.External,
            PlayerName = chosen.Name,
            PlayerUuid = chosen.Id,
            ServerAddress = server.AbsoluteUri,
            ServerName = serverName,
            ExpiresAt = DateTimeOffset.UtcNow
        };
        var tokens = new AccountTokens { AccessToken = accessToken, ClientToken = clientToken };
        if (selected == null || selected.Id != chosen.Id)
        {
            tokens = await RefreshExternalAsync(account, tokens, cancellationToken);
        }
        storage.SaveAccount(account, tokens);
        return account;
    }

    public async Task<AuthenticatedSession> GetSessionAsync(LauncherAccount account, Action<string> report, CancellationToken cancellationToken)
    {
        var tokens = LauncherStorage.Unprotect(account);
        if (account.Kind == AccountKind.Microsoft)
        {
            if (!string.Equals(account.MicrosoftClientId, MicrosoftAccountAuthentication.ClientId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("此账号保存自旧版登录方式，请在账号页重新进行 Microsoft 登录。");
            }
            if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                account = await new MicrosoftAccountAuthentication(storage, client).RefreshAsync(account, tokens, report, cancellationToken);
                tokens = LauncherStorage.Unprotect(account);
            }
        }
        else
        {
            report("正在校验外置认证会话");
            var server = NormalizeServer(account.ServerAddress);
            using var response = await client.PostAsJsonAsync(new Uri(server, "authserver/validate"),
                new { accessToken = tokens.AccessToken, clientToken = tokens.ClientToken }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
                {
                    throw new InvalidOperationException($"认证服务器暂时无法校验账号（HTTP {(int)response.StatusCode}）。");
                }
                tokens = await RefreshExternalAsync(account, tokens, cancellationToken);
                storage.SaveAccount(account, tokens);
            }
        }
        account.LastUsedAt = DateTimeOffset.UtcNow;
        return new AuthenticatedSession(account, tokens);
    }

    public async Task RemoveAccountAsync(LauncherAccount account, CancellationToken cancellationToken)
    {
        if (account.Kind == AccountKind.External)
        {
            try
            {
                var tokens = LauncherStorage.Unprotect(account);
                using var response = await client.PostAsJsonAsync(new Uri(NormalizeServer(account.ServerAddress), "authserver/invalidate"),
                    new { accessToken = tokens.AccessToken, clientToken = tokens.ClientToken }, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
            }
        }
        storage.RemoveAccount(account);
    }

    private async Task<AccountTokens> RefreshExternalAsync(LauncherAccount account, AccountTokens tokens, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(new Uri(NormalizeServer(account.ServerAddress), "authserver/refresh"), new
        {
            accessToken = tokens.AccessToken,
            clientToken = tokens.ClientToken,
            selectedProfile = new { id = account.PlayerUuid, name = account.PlayerName },
            requestUser = true
        }, cancellationToken);
        using var document = await ReadJsonAsync(response, cancellationToken);
        EnsureSuccess(response, document.RootElement, "刷新外置认证会话，请重新登录后重试");
        if (!document.RootElement.TryGetProperty("selectedProfile", out var selectedElement) ||
            ReadProfile(selectedElement).Id != account.PlayerUuid)
        {
            throw new InvalidDataException("认证服务器返回的角色与所选角色不一致。");
        }
        account.PlayerName = ReadProfile(selectedElement).Name;
        return new AccountTokens
        {
            AccessToken = RequiredString(document.RootElement, "accessToken"),
            ClientToken = OptionalString(document.RootElement, "clientToken", tokens.ClientToken)
        };
    }

    public static Uri NormalizeServer(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException("请填写完整的 HTTPS Yggdrasil API 根地址，例如 https://skin.example/api/yggdrasil/。本机调试地址可使用 HTTP。");
        }
        return uri;
    }

    private static GameProfile ReadProfile(JsonElement element)
    {
        var id = RequiredString(element, "id");
        if (!Guid.TryParse(id, out var uuid))
        {
            throw new InvalidDataException("认证服务器返回了无效的角色 UUID。");
        }
        return new GameProfile(uuid.ToString("N"), RequiredString(element, "name"));
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        return string.IsNullOrEmpty(value) ? throw new InvalidDataException($"认证响应缺少字段：{name}") : value;
    }

    private static string OptionalString(JsonElement element, string name, string fallback = "")
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    }

    private static void EnsureSuccess(HttpResponseMessage response, JsonElement body, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var error = OptionalString(body, "error");
        var explanation = error switch
        {
            "invalid_client" or "unauthorized_client" => "认证服务器拒绝了当前客户端，请检查该认证站的接入设置。",
            "invalid_grant" or "interaction_required" => "会话已失效，请重新登录。",
            "ForbiddenOperationException" => "账号、密码或会话无效。",
            _ => "请检查网络、账号状态及认证服务器地址。"
        };
        throw new InvalidOperationException($"{operation}失败（HTTP {(int)response.StatusCode}）。{explanation}");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, cancellationToken);
        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"认证服务返回了无法识别的响应（HTTP {(int)response.StatusCode}）。", exception);
        }
    }

    public void Dispose()
    {
        client.Dispose();
    }
}
