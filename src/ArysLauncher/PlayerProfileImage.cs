using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArysLauncher;

internal sealed record PlayerProfileImage(string Hash, int X, int Y, int Width, int Height)
{
    public const int MaximumBytes = 5 * 1024 * 1024;
    public const int MaximumEdge = 8192;
    public const long MaximumPixels = 16L * 1024 * 1024;

    public string Reference => $"arysprofile:{Hash}:{X}:{Y}:{Width}:{Height}";

    public static PlayerProfileImage? Parse(string? value)
    {
        if (value == null || value.Length > 512)
        {
            return null;
        }
        var fields = value.Split(':');
        if (fields.Length != 6 || fields[0] != "arysprofile" || fields[1].Length != 64 ||
            fields[1].Any(character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')) ||
            !int.TryParse(fields[2], out var x) || !int.TryParse(fields[3], out var y) ||
            !int.TryParse(fields[4], out var width) || !int.TryParse(fields[5], out var height) ||
            x < 0 || y < 0 || width <= 0 || height <= 0 || x > MaximumEdge || y > MaximumEdge ||
            width > MaximumEdge || height > MaximumEdge)
        {
            return null;
        }
        return new PlayerProfileImage(fields[1], x, y, width, height);
    }

    public bool Fits(BitmapSource image)
    {
        return (long)X + Width <= image.PixelWidth && (long)Y + Height <= image.PixelHeight;
    }

    public static async Task<ProfileImageSource> ReadAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumBytes)
        {
            throw new InvalidDataException(LauncherText.Get("ProfileImageSizeLimit"));
        }
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, token);
        return await Task.Run(() => Decode(bytes), token);
    }

    public static ProfileImageSource Decode(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes)
        {
            throw new InvalidDataException(LauncherText.Get("ProfileImageSizeLimit"));
        }
        var (width, height) = ReadDimensions(bytes);
        if (width <= 0 || height <= 0 || width > MaximumEdge || height > MaximumEdge || (long)width * height > MaximumPixels)
        {
            throw new InvalidDataException(LauncherText.Get("ProfileImageDimensionLimit"));
        }
        using var input = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder) || decoder.Frames.Count != 1)
        {
            throw new InvalidDataException(LauncherText.Get("ProfileImageFormatLimit"));
        }
        var image = decoder.Frames[0];
        if (image.PixelWidth != width || image.PixelHeight != height)
        {
            throw new InvalidDataException(LauncherText.Get("ProfileImageFormatLimit"));
        }
        image.Freeze();
        return new ProfileImageSource(bytes, image, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public BitmapSource Crop(BitmapSource image)
    {
        if (!Fits(image))
        {
            throw new InvalidDataException(LauncherText.Get("ProfileImageDimensionLimit"));
        }
        var cropped = new CroppedBitmap(image, new Int32Rect(X, Y, Width, Height));
        var factor = Math.Min(1, Math.Min(2048.0 / Width, 1024.0 / Height));
        BitmapSource preview = factor < 1 ? new TransformedBitmap(cropped, new ScaleTransform(factor, factor)) : cropped;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(preview));
        using var bytes = new MemoryStream();
        encoder.Save(bytes);
        bytes.Position = 0;
        var result = BitmapFrame.Create(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        result.Freeze();
        return result;
    }

    private static (int Width, int Height) ReadDimensions(byte[] data)
    {
        if (data.Length >= 24 && data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            return (BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4)));
        }
        if (data.Length >= 4 && data[0] == 255 && data[1] == 216)
        {
            var offset = 2;
            while (offset < data.Length)
            {
                if (data[offset++] != 255)
                {
                    break;
                }
                while (offset < data.Length && data[offset] == 255)
                {
                    offset++;
                }
                if (offset >= data.Length)
                {
                    break;
                }
                var marker = data[offset++];
                if (marker is 216 or 1 or >= 208 and <= 215)
                {
                    continue;
                }
                if (marker is 217 or 218 || offset + 2 > data.Length)
                {
                    break;
                }
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                if (length < 2 || offset + length > data.Length)
                {
                    break;
                }
                if (marker is >= 192 and <= 207 && marker is not (196 or 200 or 204) && length >= 8)
                {
                    return (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 5, 2)),
                        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 3, 2)));
                }
                offset += length;
            }
        }
        throw new InvalidDataException(LauncherText.Get("ProfileImageFormatLimit"));
    }
}

internal sealed record ProfileImageSource(byte[] Bytes, BitmapSource Image, string Hash);
