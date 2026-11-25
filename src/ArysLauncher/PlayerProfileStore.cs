using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace ArysLauncher;

internal sealed class PlayerProfileDocument
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("playerId")]
    public string PlayerId { get; set; } = "";
    [JsonPropertyName("profileKey")]
    public string ProfileKey { get; set; } = "";
    [JsonPropertyName("revision")]
    public string Revision { get; set; } = "";
    [JsonPropertyName("avatarUrl")]
    public string AvatarUrl { get; set; } = "";
    [JsonPropertyName("bannerUrl")]
    public string BannerUrl { get; set; } = "";
    [JsonPropertyName("avatarSet")]
    public bool AvatarSet { get; set; }
    [JsonPropertyName("bannerSet")]
    public bool BannerSet { get; set; }
}

internal sealed class PlayerProfileStore
{
    private readonly LauncherStorage storage;

    public string DirectoryPath => Path.Combine(storage.Root, "player-profiles");

    public PlayerProfileStore(LauncherStorage storage)
    {
        this.storage = storage;
    }

    public string ProfilePath(LauncherAccount account)
    {
        return UpdatePaths.Target(DirectoryPath, ProfileKey(account) + ".json");
    }

    public static string PlayerId(LauncherAccount account)
    {
        return Guid.Parse(account.PlayerUuid).ToString("N");
    }

    public static string ProfileKey(LauncherAccount account)
    {
        var realm = "microsoft";
        if (account.Kind == AccountKind.External)
        {
            var server = AccountService.NormalizeServer(account.ServerAddress);
            realm = server.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant() + server.AbsolutePath.TrimEnd('/');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(realm + "\n" + PlayerId(account)))).ToLowerInvariant();
    }

    public async Task<PlayerProfileDocument> ReadAsync(LauncherAccount account, CancellationToken token)
    {
        var path = ProfilePath(account);
        if (!File.Exists(path))
        {
            return new PlayerProfileDocument { PlayerId = PlayerId(account), ProfileKey = ProfileKey(account) };
        }
        if (new FileInfo(path).Length > 65536)
        {
            throw new InvalidDataException(LauncherText.Get("PlayerProfileInvalid"));
        }
        var document = JsonSerializer.Deserialize<PlayerProfileDocument>(await ReadTextAsync(path, token), LauncherStorage.JsonOptions);
        if (document == null || document.SchemaVersion != 1 || document.PlayerId != PlayerId(account) || document.ProfileKey != ProfileKey(account) ||
            !Guid.TryParse(document.Revision, out _) || !ValidReference(document.AvatarUrl) || !ValidReference(document.BannerUrl))
        {
            throw new InvalidDataException(LauncherText.Get("PlayerProfileInvalid"));
        }
        return document;
    }

    public async Task EnsureAsync(LauncherAccount account, CancellationToken token)
    {
        await ChangeAsync(account, null, null, token);
    }

    public async Task SaveAsync(LauncherAccount account, bool avatar, ProfileImageSource? image, PlayerProfileImage? crop, CancellationToken token)
    {
        if (image != null)
        {
            if (crop == null || crop.Hash != image.Hash || !crop.Fits(image.Image))
            {
                throw new InvalidDataException(LauncherText.Get("ProfileImageDimensionLimit"));
            }
            var path = ImagePath(image.Hash);
            await WriteImageAsync(path, image.Bytes, image.Hash, token);
        }
        await ChangeAsync(account, avatar, crop?.Reference ?? "", token);
    }

    public async Task<BitmapSource?> ReadPreviewAsync(string reference, CancellationToken token)
    {
        var crop = PlayerProfileImage.Parse(reference);
        if (crop == null)
        {
            return null;
        }
        var path = ImagePath(crop.Hash);
        if (!File.Exists(path))
        {
            return null;
        }
        var source = await PlayerProfileImage.ReadAsync(path, token);
        if (source.Hash != crop.Hash)
        {
            throw new InvalidDataException(LauncherText.Get("PlayerProfileInvalid"));
        }
        return crop.Crop(source.Image);
    }

    private string ImagePath(string hash)
    {
        return UpdatePaths.Target(DirectoryPath, "images/" + hash + ".img");
    }

    private async Task ChangeAsync(LauncherAccount account, bool? avatar, string? reference, CancellationToken token)
    {
        Directory.CreateDirectory(DirectoryPath);
        var lockPath = UpdatePaths.Target(DirectoryPath, ProfileKey(account) + ".lock");
        await using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        var acquired = false;
        try
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    gate.Lock(0, 1);
                    acquired = true;
                    break;
                }
                catch (IOException) when (attempt < 39)
                {
                    await Task.Delay(50, token);
                }
            }
            var path = ProfilePath(account);
            if (avatar == null && File.Exists(path))
            {
                await ReadAsync(account, token);
                return;
            }
            var document = File.Exists(path) ? await ReadAsync(account, token) : await ImportLegacyAsync(account, token);
            if (avatar == true)
            {
                document.AvatarUrl = reference ?? "";
                document.AvatarSet = true;
            }
            else if (avatar == false)
            {
                document.BannerUrl = reference ?? "";
                document.BannerSet = true;
            }
            document.Revision = Guid.NewGuid().ToString();
            await WriteAtomicAsync(path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, LauncherStorage.JsonOptions)), token);
        }
        finally
        {
            if (acquired)
            {
                gate.Unlock(0, 1);
            }
        }
    }

    private async Task<PlayerProfileDocument> ImportLegacyAsync(LauncherAccount account, CancellationToken token)
    {
        var result = new PlayerProfileDocument { PlayerId = PlayerId(account), ProfileKey = ProfileKey(account) };
        var legacy = UpdatePaths.Target(storage.GameDirectory, "config/arysdungeon-profile.json");
        if (!File.Exists(legacy) || new FileInfo(legacy).Length > 65536)
        {
            return result;
        }
        using var json = JsonDocument.Parse(await ReadTextAsync(legacy, token));
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("playerId", out var owner) || owner.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(owner.GetString(), out var ownerId) || ownerId.ToString("N") != PlayerId(account) ||
            !json.RootElement.TryGetProperty("profileKey", out var realm) || realm.ValueKind != JsonValueKind.String || realm.GetString() != ProfileKey(account))
        {
            return result;
        }
        foreach (var avatar in new[] { true, false })
        {
            if (!json.RootElement.TryGetProperty(avatar ? "avatarUrl" : "bannerUrl", out var property) || property.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var value = property.GetString() ?? "";
            var crop = PlayerProfileImage.Parse(value);
            if (crop != null)
            {
                var cached = UpdatePaths.Target(storage.GameDirectory, "arysdungeon-cache/profile-images/" + crop.Hash + ".img");
                if (!File.Exists(cached))
                {
                    continue;
                }
                var image = await PlayerProfileImage.ReadAsync(cached, token);
                if (image.Hash != crop.Hash || !crop.Fits(image.Image))
                {
                    continue;
                }
                await WriteImageAsync(ImagePath(crop.Hash), image.Bytes, image.Hash, token);
            }
            if (ValidReference(value) && value.Length > 0)
            {
                if (avatar)
                {
                    result.AvatarUrl = value;
                    result.AvatarSet = true;
                }
                else
                {
                    result.BannerUrl = value;
                    result.BannerSet = true;
                }
            }
        }
        return result;
    }

    private static bool ValidReference(string? reference)
    {
        return reference != null && (reference.Length == 0 || reference.Length <= 512 && (PlayerProfileImage.Parse(reference) != null ||
            Uri.TryCreate(reference, UriKind.Absolute, out var address) && address.Scheme is "https" or "http"));
    }

    private static async Task<string> ReadTextAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > 65536)
        {
            throw new InvalidDataException(LauncherText.Get("PlayerProfileInvalid"));
        }
        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, token);
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    private static async Task WriteImageAsync(string path, byte[] bytes, string hash, CancellationToken token)
    {
        if (File.Exists(path) && new FileInfo(path).Length == bytes.Length)
        {
            await using var input = File.OpenRead(path);
            if (Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
        {
            var files = Directory.EnumerateFiles(directory, "*.img").Take(4097).ToArray();
            if (files.Length > 4096 || files.Sum(file => new FileInfo(file).Length) + bytes.Length > 256L * 1024 * 1024)
            {
                throw new IOException(LauncherText.Get("ProfileStorageFull"));
            }
        }
        await WriteAtomicAsync(path, bytes, token);
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, token);
                await output.FlushAsync(token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
