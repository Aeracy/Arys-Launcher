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
            throw new InvalidOperationException(LauncherText.Get("ExternalCredentialsRequired"));
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
        EnsureSuccess(response, document.RootElement, LauncherText.Get("ExternalSignIn"));
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
            throw new InvalidOperationException(LauncherText.Get("ExternalCharacterMissing"));
        }
        var chosen = profiles.Count == 1 ? profiles[0] : await chooseProfile(profiles);
        if (profiles.All(profile => profile.Id != chosen.Id))
        {
            throw new InvalidDataException(LauncherText.Get("ExternalCharacterMismatch"));
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

    public async Task<AuthenticatedSession> GetSessionAsync(LauncherAccount account, Action<string> report,
        CancellationToken cancellationToken, bool needsXboxProfile = false)
    {
        await storage.AccountSessionGate.WaitAsync(cancellationToken);
        try
        {
            account = storage.Accounts.FirstOrDefault(current => current.Id == account.Id) ??
                throw new InvalidOperationException(LauncherText.Get("AccountRemoved"));
            return await GetSessionCoreAsync(account, report, cancellationToken, needsXboxProfile);
        }
        finally
        {
            storage.AccountSessionGate.Release();
        }
    }

    private async Task<AuthenticatedSession> GetSessionCoreAsync(LauncherAccount account, Action<string> report,
        CancellationToken cancellationToken, bool needsXboxProfile)
    {
        var tokens = LauncherStorage.Unprotect(account);
        if (account.Kind == AccountKind.Microsoft)
        {
            if (!string.Equals(account.MicrosoftClientId, MicrosoftAccountAuthentication.ClientId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(LauncherText.Get("LegacyMicrosoftAccount"));
            }
            if (account.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5) || needsXboxProfile &&
                (string.IsNullOrWhiteSpace(tokens.XboxUserToken) || tokens.XboxUserTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5)))
            {
                account = await new MicrosoftAccountAuthentication(storage, client).RefreshAsync(account, tokens, report, cancellationToken);
                tokens = LauncherStorage.Unprotect(account);
            }
        }
        else
        {
            report(LauncherText.Get("ExternalSessionChecking"));
            var server = NormalizeServer(account.ServerAddress);
            using var response = await client.PostAsJsonAsync(new Uri(server, "authserver/validate"),
                new { accessToken = tokens.AccessToken, clientToken = tokens.ClientToken }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
                {
                    throw new InvalidOperationException(LauncherText.Get("ExternalSessionCheckFailed", (int)response.StatusCode));
                }
                tokens = await RefreshExternalAsync(account, tokens, cancellationToken);
                storage.SaveAccount(account, tokens, selectAccount: false);
            }
        }
        account.LastUsedAt = DateTimeOffset.UtcNow;
        return new AuthenticatedSession(account, tokens);
    }

    public async Task RemoveAccountAsync(LauncherAccount account, CancellationToken cancellationToken)
    {
        await storage.AccountSessionGate.WaitAsync(cancellationToken);
        try
        {
            await RemoveAccountCoreAsync(account, cancellationToken);
        }
        finally
        {
            storage.AccountSessionGate.Release();
        }
    }

    private async Task RemoveAccountCoreAsync(LauncherAccount account, CancellationToken cancellationToken)
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
        EnsureSuccess(response, document.RootElement, LauncherText.Get("ExternalSessionRefresh"));
        if (!document.RootElement.TryGetProperty("selectedProfile", out var selectedElement) ||
            ReadProfile(selectedElement).Id != account.PlayerUuid)
        {
            throw new InvalidDataException(LauncherText.Get("ExternalSessionCharacterMismatch"));
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
            throw new InvalidOperationException(LauncherText.Get("YggdrasilAddressInvalid"));
        }
        return uri;
    }

    private static GameProfile ReadProfile(JsonElement element)
    {
        var id = RequiredString(element, "id");
        if (!Guid.TryParse(id, out var uuid))
        {
            throw new InvalidDataException(LauncherText.Get("ExternalUuidInvalid"));
        }
        return new GameProfile(uuid.ToString("N"), RequiredString(element, "name"));
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        return string.IsNullOrEmpty(value) ? throw new InvalidDataException(LauncherText.Get("AuthResponseFieldMissing", name)) : value;
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
            "invalid_client" or "unauthorized_client" => LauncherText.Get("ExternalClientRejected"),
            "invalid_grant" or "interaction_required" => LauncherText.Get("SessionExpired"),
            "ForbiddenOperationException" => LauncherText.Get("ExternalCredentialsInvalid"),
            _ => LauncherText.Get("ExternalTroubleshoot")
        };
        throw new InvalidOperationException(LauncherText.Get("AuthHttpFailure", operation, (int)response.StatusCode, explanation));
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
            throw new InvalidOperationException(LauncherText.Get("AuthResponseUnrecognized", (int)response.StatusCode), exception);
        }
    }

    public void Dispose()
    {
        client.Dispose();
    }
}
