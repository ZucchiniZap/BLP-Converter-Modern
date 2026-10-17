using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SereniaBLPLib;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AvaloniaApplication1;

public enum BlpCompression
{
    Raw,
    DXT1,
    DXT3,
    DXT5
}

public enum BlpAlphaDepth
{
    Opaque0Bit = 0,
    OneBit = 1,
    FourBit = 4,
    EightBit = 8
}

public sealed record BlpConversionOptions(
    BlpCompression Compression,
    BlpAlphaDepth AlphaDepth,
    bool GenerateMipmaps,
    bool UseColorKeyAwareMipmapping,
    string ColorKeyHex);

public static class BlpConversionService
{
    public static void Convert(string inputPath, string outputPath, BlpConversionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        var inputExtension = Path.GetExtension(inputPath);
        if (inputExtension.Equals(".blp", StringComparison.OrdinalIgnoreCase))
        {
            if (!Path.GetExtension(outputPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("BLP input must be saved as a PNG file.");

            ConvertBlpToPng(inputPath, outputPath);
            return;
        }

        if (!inputExtension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only PNG and BLP input files are supported.");
        if (!Path.GetExtension(outputPath).Equals(".blp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("PNG input must be saved as a BLP file.");

        ConvertPngToBlp(inputPath, outputPath, options);
    }

    private static void ConvertBlpToPng(string inputPath, string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        using var stream = File.OpenRead(inputPath);
        using var blp = new BlpFile(stream);
        var pixels = blp.GetPixels(0, out var width, out var height, bgra: false);
        using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
        image.SaveAsPng(outputPath);
    }

    private static void ConvertPngToBlp(string inputPath, string outputPath, BlpConversionOptions options)
    {
        ValidateOptions(options);
        var colorKey = options.UseColorKeyAwareMipmapping
            ? ParseColorKey(options.ColorKeyHex)
            : new Rgba32(0, 0, 0, byte.MaxValue);
        using var baseImage = Image.Load<Rgba32>(inputPath);
        NormalizeAlpha(baseImage, options.AlphaDepth);

        var levels = new List<Image<Rgba32>> { baseImage.Clone() };
        try
        {
            if (options.GenerateMipmaps)
            {
                while (levels[^1].Width > 1 || levels[^1].Height > 1)
                {
                    if (levels.Count == 16)
                        throw new InvalidOperationException("This image needs more than the 16 mip levels supported by BLP.");
                    var current = levels[^1];
                    levels.Add(Downsample(current, options.UseColorKeyAwareMipmapping, colorKey));
                }
            }

            WriteBlp(outputPath, levels, options.Compression, options.AlphaDepth);
        }
        finally
        {
            foreach (var level in levels)
                level.Dispose();
        }
    }

    private static Image<Rgba32> Downsample(Image<Rgba32> source, bool colorKeyAware, Rgba32 colorKey)
    {
        var width = Math.Max(1, source.Width / 2);
        var height = Math.Max(1, source.Height / 2);
        var result = new Image<Rgba32>(width, height);
        var sourcePixels = new Rgba32[checked(source.Width * source.Height)];
        source.CopyPixelDataTo(sourcePixels);

        result.ProcessPixelRows(destinationAccessor =>
        {
            Span<Rgba32> block = stackalloc Rgba32[4];
            for (var y = 0; y < height; y++)
            {
                var destinationRow = destinationAccessor.GetRowSpan(y);
                var sourceY = y * 2;
                var row0Offset = Math.Min(sourceY, source.Height - 1) * source.Width;
                var row1Offset = Math.Min(sourceY + 1, source.Height - 1) * source.Width;

                for (var x = 0; x < width; x++)
                {
                    var sourceX = x * 2;
                    block[0] = sourcePixels[row0Offset + Math.Min(sourceX, source.Width - 1)];
                    block[1] = sourcePixels[row0Offset + Math.Min(sourceX + 1, source.Width - 1)];
                    block[2] = sourcePixels[row1Offset + Math.Min(sourceX, source.Width - 1)];
                    block[3] = sourcePixels[row1Offset + Math.Min(sourceX + 1, source.Width - 1)];
                    destinationRow[x] = AverageBlock(block, colorKeyAware, colorKey);
                }
            }
        });

        return result;
    }

    private static Rgba32 AverageBlock(ReadOnlySpan<Rgba32> pixels, bool colorKeyAware, Rgba32 colorKey)
    {
        if (colorKeyAware)
        {
            var leafCount = 0;
            var red = 0;
            var green = 0;
            var blue = 0;
            var alpha = 0;
            foreach (var pixel in pixels)
            {
                if (pixel.R == colorKey.R && pixel.G == colorKey.G && pixel.B == colorKey.B)
                    continue;

                leafCount++;
                red += pixel.R;
                green += pixel.G;
                blue += pixel.B;
                alpha += pixel.A;
            }

            if (leafCount < 2)
                return new Rgba32(colorKey.R, colorKey.G, colorKey.B, byte.MaxValue);

            return new Rgba32(
                (byte)(red / leafCount),
                (byte)(green / leafCount),
                (byte)(blue / leafCount),
                (byte)(alpha / leafCount));
        }

        var totalRed = 0;
        var totalGreen = 0;
        var totalBlue = 0;
        var totalAlpha = 0;
        foreach (var pixel in pixels)
        {
            totalRed += pixel.R;
            totalGreen += pixel.G;
            totalBlue += pixel.B;
            totalAlpha += pixel.A;
        }

        return new Rgba32(
            (byte)(totalRed / pixels.Length),
            (byte)(totalGreen / pixels.Length),
            (byte)(totalBlue / pixels.Length),
            (byte)(totalAlpha / pixels.Length));
    }

    private static void NormalizeAlpha(Image<Rgba32> image, BlpAlphaDepth alphaDepth)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    pixel.A = alphaDepth switch
                    {
                        BlpAlphaDepth.Opaque0Bit => byte.MaxValue,
                        BlpAlphaDepth.OneBit => pixel.A < 128 ? (byte)0 : byte.MaxValue,
                        BlpAlphaDepth.FourBit => (byte)((pixel.A + 8) / 17 * 17),
                        _ => pixel.A
                    };
                    row[x] = pixel;
                }
            }
        });
    }

    private static void ValidateOptions(BlpConversionOptions options)
    {
        if (options.AlphaDepth is not (BlpAlphaDepth.Opaque0Bit or BlpAlphaDepth.OneBit or BlpAlphaDepth.FourBit or BlpAlphaDepth.EightBit))
            throw new ArgumentOutOfRangeException(nameof(options), "Alpha depth must be 0, 1, 4, or 8 bits.");

        var valid = options.Compression switch
        {
            BlpCompression.Raw => true,
            BlpCompression.DXT1 => options.AlphaDepth is BlpAlphaDepth.Opaque0Bit or BlpAlphaDepth.OneBit,
            BlpCompression.DXT3 => options.AlphaDepth == BlpAlphaDepth.FourBit,
            BlpCompression.DXT5 => options.AlphaDepth == BlpAlphaDepth.EightBit,
            _ => false
        };
        if (!valid)
            throw new ArgumentException("DXT1 supports 0- or 1-bit alpha, DXT3 requires 4-bit alpha, and DXT5 requires 8-bit alpha.", nameof(options));
    }

    private static Rgba32 ParseColorKey(string value)
    {
        var hex = value.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            throw new FormatException("Color key must be a six-digit hex value, such as #000000.");

        return new Rgba32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, byte.MaxValue);
    }

    private static void WriteBlp(string outputPath, IReadOnlyList<Image<Rgba32>> levels, BlpCompression compression, BlpAlphaDepth alphaDepth)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x32504C42); // BLP2
        writer.Write(1u);
        writer.Write((byte)(compression == BlpCompression.Raw ? BlpColorEncoding.Argb8888 : BlpColorEncoding.Dxt));
        writer.Write((byte)alphaDepth);
        writer.Write((byte)(compression switch
        {
            BlpCompression.DXT3 => BlpPixelFormat.Dxt3,
            BlpCompression.DXT5 => BlpPixelFormat.Dxt5,
            BlpCompression.Raw => BlpPixelFormat.Argb8888,
            _ => BlpPixelFormat.Dxt1
        }));
        writer.Write((byte)(levels.Count > 1 ? 1 : 0));
        writer.Write(levels[0].Width);
        writer.Write(levels[0].Height);

        var offsetPosition = stream.Position;
        for (var i = 0; i < 16; i++)
            writer.Write(0u);
        var sizePosition = stream.Position;
        for (var i = 0; i < 16; i++)
            writer.Write(0u);

        var offsets = new uint[16];
        var sizes = new uint[16];
        for (var index = 0; index < levels.Count; index++)
        {
            offsets[index] = checked((uint)stream.Position);
            sizes[index] = compression == BlpCompression.Raw
                ? checked((uint)(levels[index].Width * levels[index].Height * 4))
                : checked((uint)(GetBlockCount(levels[index].Width) * GetBlockCount(levels[index].Height) * (compression == BlpCompression.DXT1 ? 8 : 16)));

            if (compression == BlpCompression.Raw)
                WriteRawPixels(writer, levels[index]);
            else
                WriteCompressedPixels(writer, levels[index], compression, alphaDepth);
        }

        var endPosition = stream.Position;
        stream.Position = offsetPosition;
        foreach (var offset in offsets)
            writer.Write(offset);
        stream.Position = sizePosition;
        foreach (var size in sizes)
            writer.Write(size);
        stream.Position = endPosition;
    }

    private static int GetBlockCount(int dimension) => (dimension + 3) / 4;

    private static void WriteRawPixels(BinaryWriter writer, Image<Rgba32> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                foreach (var pixel in accessor.GetRowSpan(y))
                {
                    writer.Write(pixel.R);
                    writer.Write(pixel.G);
                    writer.Write(pixel.B);
                    writer.Write(pixel.A);
                }
            }
        });
    }

    private static void WriteCompressedPixels(BinaryWriter writer, Image<Rgba32> image, BlpCompression compression, BlpAlphaDepth alphaDepth)
    {
        image.ProcessPixelRows(accessor =>
        {
            Span<Rgba32> pixels = stackalloc Rgba32[16];
            for (var blockY = 0; blockY < GetBlockCount(image.Height); blockY++)
            {
                for (var blockX = 0; blockX < GetBlockCount(image.Width); blockX++)
                {
                    for (var y = 0; y < 4; y++)
                    {
                        var sourceY = Math.Min(blockY * 4 + y, image.Height - 1);
                        var row = accessor.GetRowSpan(sourceY);
                        for (var x = 0; x < 4; x++)
                            pixels[y * 4 + x] = row[Math.Min(blockX * 4 + x, image.Width - 1)];
                    }

                    if (compression == BlpCompression.DXT3)
                        WriteDxt3Alpha(writer, pixels);
                    else if (compression == BlpCompression.DXT5)
                        WriteDxt5Alpha(writer, pixels);

                    WriteDxtColor(writer, pixels, compression == BlpCompression.DXT1 && alphaDepth == BlpAlphaDepth.OneBit);
                }
            }
        });
    }

    private static void WriteDxt3Alpha(BinaryWriter writer, ReadOnlySpan<Rgba32> pixels)
    {
        ulong bits = 0;
        for (var i = 0; i < pixels.Length; i++)
            bits |= (ulong)((pixels[i].A + 8) / 17) << (i * 4);
        writer.Write(bits);
    }

    private static void WriteDxt5Alpha(BinaryWriter writer, ReadOnlySpan<Rgba32> pixels)
    {
        byte maximum = 0;
        byte minimum = byte.MaxValue;
        foreach (var pixel in pixels)
        {
            maximum = Math.Max(maximum, pixel.A);
            minimum = Math.Min(minimum, pixel.A);
        }

        writer.Write(maximum);
        writer.Write(minimum);
        Span<byte> palette = stackalloc byte[8];
        palette[0] = maximum;
        palette[1] = minimum;
        for (var i = 1; i <= 6; i++)
            palette[i + 1] = (byte)(((7 - i) * maximum + i * minimum) / 7);

        ulong indices = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var bestIndex = 0;
            var bestDistance = int.MaxValue;
            for (var candidate = 0; candidate < palette.Length; candidate++)
            {
                var distance = Math.Abs(pixels[i].A - palette[candidate]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = candidate;
                }
            }
            indices |= (ulong)bestIndex << (i * 3);
        }

        for (var i = 0; i < 6; i++)
            writer.Write((byte)(indices >> (i * 8)));
    }

    private static void WriteDxtColor(BinaryWriter writer, ReadOnlySpan<Rgba32> pixels, bool punchThroughAlpha)
    {
        var transparent = punchThroughAlpha && pixels.ContainsAnyAlphaBelow128();
        var darkest = pixels[0];
        var lightest = pixels[0];
        foreach (var pixel in pixels)
        {
            if (Brightness(pixel) < Brightness(darkest))
                darkest = pixel;
            if (Brightness(pixel) > Brightness(lightest))
                lightest = pixel;
        }

        var first = Pack565(lightest);
        var second = Pack565(darkest);
        if (transparent)
        {
            if (first > second)
                (first, second) = (second, first);
        }
        else if (first <= second)
        {
            if (first < ushort.MaxValue)
                first = (ushort)(second + (second < ushort.MaxValue ? 1 : 0));
            if (first <= second)
                (first, second) = (second, first);
        }

        writer.Write(first);
        writer.Write(second);
        Span<Rgba32> palette = stackalloc Rgba32[4];
        palette[0] = Unpack565(first);
        palette[1] = Unpack565(second);
        if (transparent)
        {
            palette[2] = Mix(palette[0], palette[1], 1, 1, 2);
            palette[3] = new Rgba32(0, 0, 0, 0);
        }
        else
        {
            palette[2] = Mix(palette[0], palette[1], 2, 1, 3);
            palette[3] = Mix(palette[0], palette[1], 1, 2, 3);
        }

        uint indices = 0;
        for (var i = 0; i < pixels.Length; i++)
        {
            var bestIndex = 0;
            var bestDistance = int.MaxValue;
            var end = transparent && pixels[i].A < 128 ? 4 : (transparent ? 3 : 4);
            for (var candidate = 0; candidate < end; candidate++)
            {
                var dr = pixels[i].R - palette[candidate].R;
                var dg = pixels[i].G - palette[candidate].G;
                var db = pixels[i].B - palette[candidate].B;
                var distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = candidate;
                }
            }
            if (transparent && pixels[i].A < 128)
                bestIndex = 3;
            indices |= (uint)bestIndex << (i * 2);
        }
        writer.Write(indices);
    }

    private static int Brightness(Rgba32 pixel) => pixel.R * 299 + pixel.G * 587 + pixel.B * 114;

    private static ushort Pack565(Rgba32 pixel) => (ushort)(((pixel.R >> 3) << 11) | ((pixel.G >> 2) << 5) | (pixel.B >> 3));

    private static Rgba32 Unpack565(ushort value) => new(
        (byte)(((value >> 11) & 31) * 255 / 31),
        (byte)(((value >> 5) & 63) * 255 / 63),
        (byte)((value & 31) * 255 / 31),
        byte.MaxValue);

    private static Rgba32 Mix(Rgba32 first, Rgba32 second, int firstWeight, int secondWeight, int divisor) => new(
        (byte)((first.R * firstWeight + second.R * secondWeight) / divisor),
        (byte)((first.G * firstWeight + second.G * secondWeight) / divisor),
        (byte)((first.B * firstWeight + second.B * secondWeight) / divisor),
        byte.MaxValue);

    private static bool ContainsAnyAlphaBelow128(this ReadOnlySpan<Rgba32> pixels)
    {
        foreach (var pixel in pixels)
        {
            if (pixel.A < 128)
                return true;
        }
        return false;
    }
}
