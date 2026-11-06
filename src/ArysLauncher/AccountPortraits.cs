using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArysLauncher;

internal sealed record AccountPortrait(BitmapSource Image, bool Pixelated);

internal sealed class AccountPortraits
{
    private readonly LauncherStorage storage;

    public AccountPortraits(LauncherStorage storage)
    {
        this.storage = storage;
    }

    public async Task LoadAsync(LauncherAccount account, Action<AccountPortrait> present, CancellationToken token)
    {
        var cached = await Task.Run(() => ReadCached(account), token);
        var fresh = cached != null && DateTime.UtcNow - cached.Value.Updated < TimeSpan.FromHours(6);
        if (cached != null)
        {
            token.ThrowIfCancellationRequested();
            present(cached.Value.Portrait);
            if (fresh && (account.Kind != AccountKind.Microsoft || !cached.Value.Portrait.Pixelated))
            {
                return;
            }
        }
        using var client = NetworkClients.Create(storage.Settings, allowRedirect: false);
        client.Timeout = TimeSpan.FromSeconds(10);
        AccountPortrait? portrait = null;
        if (account.Kind == AccountKind.Microsoft)
        {
            try
            {
                var url = await ReadXboxPictureAsync(account, client, token);
                var bytes = await ReadImageAsync(client, url, token);
                portrait = await Task.Run(() => Decode(bytes, skin: false), token);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or
                InvalidOperationException or FormatException or CryptographicException or NotSupportedException or
                ArgumentException or KeyNotFoundException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                if (cached is { Portrait.Pixelated: false } || fresh)
                {
                    return;
                }
            }
        }
        if (portrait == null)
        {
            var server = account.Kind == AccountKind.Microsoft ? new Uri("https://sessionserver.mojang.com/") :
                new Uri(AccountService.NormalizeServer(account.ServerAddress), "sessionserver/");
            var uuid = Guid.Parse(account.PlayerUuid).ToString("N");
            using var profile = await ReadJsonAsync(client, new HttpRequestMessage(HttpMethod.Get,
                new Uri(server, $"session/minecraft/profile/{uuid}?unsigned=true")), token);
            if (!Guid.TryParse(profile.RootElement.GetProperty("id").GetString(), out var returnedId) || returnedId.ToString("N") != uuid)
            {
                throw new InvalidDataException(LauncherText.Get("SkinProfileMismatch"));
            }
            var property = profile.RootElement.GetProperty("properties").EnumerateArray()
                .First(item => item.GetProperty("name").GetString() == "textures");
            var encoded = property.GetProperty("value").GetString() ?? "";
            if (encoded.Length > 128 * 1024)
            {
                throw new InvalidDataException(LauncherText.Get("SkinDescriptionTooLarge"));
            }
            using var textures = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var url = ImageAddress(textures.RootElement.GetProperty("textures").GetProperty("SKIN").GetProperty("url").GetString());
            var bytes = await ReadImageAsync(client, url, token);
            portrait = await Task.Run(() => Decode(bytes, skin: true), token);
        }
        token.ThrowIfCancellationRequested();
        present(portrait);
        await Task.Run(() => Save(account, portrait, token), token);
    }

    public void Remove(LauncherAccount account)
    {
        foreach (var skin in new[] { false, true })
        {
            File.Delete(CachePath(account, skin));
        }
    }

    private async Task<Uri> ReadXboxPictureAsync(LauncherAccount account, HttpClient client, CancellationToken token)
    {
        using var accounts = new AccountService(storage);
        var session = await accounts.GetSessionAsync(account, _ => { }, token, needsXboxProfile: true);
        var xuid = session.Tokens.Xuid;
        if (string.IsNullOrEmpty(xuid) || xuid.Any(character => character < '0' || character > '9'))
        {
            throw new InvalidDataException(LauncherText.Get("XboxUserIdMissing"));
        }
        using var xsts = await ReadJsonAsync(client, new HttpRequestMessage(HttpMethod.Post,
            "https://xsts.auth.xboxlive.com/xsts/authorize")
        {
            Content = JsonContent.Create(new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { session.Tokens.XboxUserToken } },
                RelyingParty = "http://xboxlive.com",
                TokenType = "JWT"
            })
        }, token);
        var claims = xsts.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0];
        var hash = claims.GetProperty("uhs").GetString();
        var credential = xsts.RootElement.GetProperty("Token").GetString();
        if (string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(credential) ||
            claims.TryGetProperty("xid", out var returnedId) && returnedId.GetString() != xuid)
        {
            throw new InvalidDataException(LauncherText.Get("XboxAvatarAccountMismatch"));
        }
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://profile.xboxlive.com/users/xuid({xuid})/profile/settings?settings=GameDisplayPicRaw");
        request.Headers.TryAddWithoutValidation("Authorization", $"XBL3.0 x={hash};{credential}");
        request.Headers.Add("x-xbl-contract-version", "2");
        using var profile = await ReadJsonAsync(client, request, token);
        var user = profile.RootElement.GetProperty("profileUsers").EnumerateArray()
            .First(item => item.GetProperty("id").GetString() == xuid);
        var picture = user.GetProperty("settings").EnumerateArray()
            .First(item => item.GetProperty("id").GetString() == "GameDisplayPicRaw");
        return ImageAddress(picture.GetProperty("value").GetString());
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        using (request)
        {
            var bytes = await ReadAsync(client, request, 512 * 1024, token);
            return JsonDocument.Parse(bytes);
        }
    }

    private static async Task<byte[]> ReadImageAsync(HttpClient client, Uri address, CancellationToken token)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                address = ImageAddress(new Uri(address, location).AbsoluteUri);
                continue;
            }
            response.EnsureSuccessStatusCode();
            return await ReadBodyAsync(response, 2 * 1024 * 1024, token);
        }
        throw new HttpRequestException(LauncherText.Get("AvatarRedirectLimit"));
    }

    private static async Task<byte[]> ReadAsync(HttpClient client, HttpRequestMessage request, int maximum, CancellationToken token)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        return await ReadBodyAsync(response, maximum, token);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum)
        {
            throw new InvalidDataException(LauncherText.Get("AvatarResponseTooLarge"));
        }
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > maximum)
            {
                throw new InvalidDataException(LauncherText.Get("AvatarResponseTooLarge"));
            }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static Uri ImageAddress(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException(LauncherText.Get("AvatarAddressInvalid"));
        }
        if (uri.Scheme == Uri.UriSchemeHttp && uri.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
        {
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        }
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            throw new InvalidDataException(LauncherText.Get("AvatarRequiresHttps"));
        }
        return uri;
    }

    private (AccountPortrait Portrait, DateTime Updated)? ReadCached(LauncherAccount account)
    {
        foreach (var skin in account.Kind == AccountKind.Microsoft ? new[] { false, true } : new[] { true })
        {
            var path = CachePath(account, skin);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 2 * 1024 * 1024)
            {
                continue;
            }
            try
            {
                return (new AccountPortrait(DecodeBitmap(File.ReadAllBytes(path), skin ? 0 : 128), skin), info.LastWriteTimeUtc);
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException or FormatException)
            {
            }
        }
        return null;
    }

    private void Save(LauncherAccount account, AccountPortrait portrait, CancellationToken token)
    {
        var path = CachePath(account, portrait.Pixelated);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(portrait.Image));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string CachePath(LauncherAccount account, bool skin)
    {
        var identity = $"{account.Kind}|{account.ServerAddress}|{account.PlayerUuid}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return UpdatePaths.Target(storage.Root, $"avatars/{key}-{(skin ? "skin" : "xbox")}.png");
    }

    private static AccountPortrait Decode(byte[] bytes, bool skin)
    {
        var bitmap = DecodeBitmap(bytes, skin ? 0 : 128);
        if (!skin)
        {
            return new AccountPortrait(bitmap, false);
        }
        if (bitmap.PixelWidth is < 64 or > 1024 || bitmap.PixelWidth % 64 != 0 ||
            bitmap.PixelHeight != bitmap.PixelWidth && bitmap.PixelHeight * 2 != bitmap.PixelWidth)
        {
            throw new InvalidDataException(LauncherText.Get("SkinDimensionsInvalid"));
        }
        var size = bitmap.PixelWidth / 8;
        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var face = new byte[size * size * 4];
        var overlay = new byte[face.Length];
        pixels.CopyPixels(new Int32Rect(size, size, size, size), face, size * 4, 0);
        pixels.CopyPixels(new Int32Rect(size * 5, size, size, size), overlay, size * 4, 0);
        var opaqueOverlay = true;
        for (var index = 3; index < overlay.Length; index += 4)
        {
            opaqueOverlay &= overlay[index] == 255;
        }
        var legacy = bitmap.PixelHeight * 2 == bitmap.PixelWidth;
        for (var index = 0; index < face.Length; index += 4)
        {
            var alpha = legacy && opaqueOverlay ? 0 : overlay[index + 3];
            for (var channel = 0; channel < 3; channel++)
            {
                face[index + channel] = (byte)((overlay[index + channel] * alpha + face[index + channel] * (255 - alpha) + 127) / 255);
            }
            face[index + 3] = 255;
        }
        var head = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, face, size * 4);
        head.Freeze();
        return new AccountPortrait(head, true);
    }

    private static BitmapSource DecodeBitmap(byte[] bytes, int width)
    {
        using var stream = new MemoryStream(bytes);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var maximum = width == 0 ? 1024 : 4096;
        if (frame.PixelWidth < 1 || frame.PixelWidth > maximum || frame.PixelHeight < 1 || frame.PixelHeight > maximum ||
            frame.PixelWidth > frame.PixelHeight * 4 || frame.PixelHeight > frame.PixelWidth * 4)
        {
            throw new InvalidDataException(LauncherText.Get("AvatarDimensionsTooLarge"));
        }
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = width;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
