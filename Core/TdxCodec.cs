using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UrukCardEditor.Core;

public static class TdxCodec
{
    public const int TargetWidth = 512;
    public const int TargetHeight = 376;
    private const int MipmapCount = 10;

    public static BitmapSource DecodeThumbnail(byte[] tdx, int targetWidth = 124, int targetHeight = 92)
    {
        if (tdx.Length < 24)
            throw new InvalidDataException("TDX texture is too small.");
        var width = BitConverter.ToUInt16(tdx, 2);
        var height = BitConverter.ToUInt16(tdx, 4);
        var format = BitConverter.ToUInt32(tdx, 12);
        if (width <= 0 || height <= 0 || format != 0x31545844u)
            throw new InvalidDataException("Only DXT1 card textures are supported for thumbnails.");

        var pixels = DecodeBc1(tdx.AsSpan(16), width, height);
        var thumbnail = ScaleCropBgra(pixels, width, height, targetWidth, targetHeight);
        var bitmap = BitmapSource.Create(targetWidth, targetHeight, 96, 96, PixelFormats.Bgra32, null, thumbnail, targetWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static void WriteCardArt(string inputPath, string outputPath)
    {
        BitmapSource source;
        using (var stream = File.OpenRead(inputPath))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            source = decoder.Frames[0];
            source.Freeze();
        }

        var targetRatio = TargetWidth / (double)TargetHeight;
        var sourceRatio = source.PixelWidth / (double)source.PixelHeight;
        Int32Rect crop;
        if (sourceRatio > targetRatio)
        {
            var width = Math.Max(1, (int)Math.Round(source.PixelHeight * targetRatio));
            crop = new Int32Rect((source.PixelWidth - width) / 2, 0, width, source.PixelHeight);
        }
        else
        {
            var height = Math.Max(1, (int)Math.Round(source.PixelWidth / targetRatio));
            crop = new Int32Rect(0, (source.PixelHeight - height) / 2, source.PixelWidth, height);
        }

        BitmapSource cropped = new CroppedBitmap(source, crop);
        BitmapSource scaled = new TransformedBitmap(cropped,
            new ScaleTransform(TargetWidth / (double)cropped.PixelWidth, TargetHeight / (double)cropped.PixelHeight));
        BitmapSource bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[TargetWidth * TargetHeight * 4];
        bgra.CopyPixels(pixels, TargetWidth * 4, 0);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var output = File.Create(outputPath);
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: false);
        writer.Write((ushort)512);
        writer.Write((ushort)TargetWidth);
        writer.Write((ushort)TargetHeight);
        writer.Write((ushort)MipmapCount);
        writer.Write(0u);
        writer.Write(0x31545844u);

        var widthNow = TargetWidth;
        var heightNow = TargetHeight;
        var current = pixels;
        for (var level = 0; level < MipmapCount; level++)
        {
            writer.Write(EncodeBc1(current, widthNow, heightNow));
            if (level + 1 == MipmapCount) break;
            var nextWidth = Math.Max(1, widthNow / 2);
            var nextHeight = Math.Max(1, heightNow / 2);
            current = Downsample(current, widthNow, heightNow, nextWidth, nextHeight);
            widthNow = nextWidth;
            heightNow = nextHeight;
        }
    }

    private static byte[] Downsample(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var result = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < targetHeight; y++)
        {
            var y0 = y * sourceHeight / targetHeight;
            var y1 = Math.Max(y0 + 1, (y + 1) * sourceHeight / targetHeight);
            for (var x = 0; x < targetWidth; x++)
            {
                var x0 = x * sourceWidth / targetWidth;
                var x1 = Math.Max(x0 + 1, (x + 1) * sourceWidth / targetWidth);
                var sums = new int[4];
                var count = 0;
                for (var sy = y0; sy < Math.Min(y1, sourceHeight); sy++)
                for (var sx = x0; sx < Math.Min(x1, sourceWidth); sx++)
                {
                    var offset = (sy * sourceWidth + sx) * 4;
                    for (var channel = 0; channel < 4; channel++) sums[channel] += source[offset + channel];
                    count++;
                }
                var destination = (y * targetWidth + x) * 4;
                for (var channel = 0; channel < 4; channel++) result[destination + channel] = (byte)(sums[channel] / count);
            }
        }
        return result;
    }

    private static byte[] EncodeBc1(byte[] pixels, int width, int height)
    {
        var blockWidth = Math.Max(1, (width + 3) / 4);
        var blockHeight = Math.Max(1, (height + 3) / 4);
        var output = new byte[blockWidth * blockHeight * 8];
        var destination = 0;
        var colors = new byte[16 * 3];
        var palette = new int[12];

        for (var by = 0; by < blockHeight; by++)
        for (var bx = 0; bx < blockWidth; bx++)
        {
            Array.Clear(colors);
            Array.Clear(palette);
            var minR = 255; var minG = 255; var minB = 255;
            var maxR = 0; var maxG = 0; var maxB = 0;
            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                var x = Math.Min(width - 1, bx * 4 + px);
                var y = Math.Min(height - 1, by * 4 + py);
                var source = (y * width + x) * 4;
                var index = (py * 4 + px) * 3;
                var b = pixels[source]; var g = pixels[source + 1]; var r = pixels[source + 2];
                colors[index] = r; colors[index + 1] = g; colors[index + 2] = b;
                minR = Math.Min(minR, r); minG = Math.Min(minG, g); minB = Math.Min(minB, b);
                maxR = Math.Max(maxR, r); maxG = Math.Max(maxG, g); maxB = Math.Max(maxB, b);
            }

            ushort color0 = To565(maxR, maxG, maxB);
            ushort color1 = To565(minR, minG, minB);
            if (color0 < color1) (color0, color1) = (color1, color0);
            if (color0 == color1) { if (color0 < ushort.MaxValue) color0++; else color1--; }
            Decode565(color0, palette, 0);
            Decode565(color1, palette, 3);
            for (var channel = 0; channel < 3; channel++)
            {
                palette[6 + channel] = (2 * palette[channel] + palette[3 + channel]) / 3;
                palette[9 + channel] = (palette[channel] + 2 * palette[3 + channel]) / 3;
            }

            uint indices = 0;
            for (var i = 0; i < 16; i++)
            {
                var r = colors[i * 3]; var g = colors[i * 3 + 1]; var b = colors[i * 3 + 2];
                var best = 0; var bestDistance = int.MaxValue;
                for (var p = 0; p < 4; p++)
                {
                    var dr = r - palette[p * 3]; var dg = g - palette[p * 3 + 1]; var db = b - palette[p * 3 + 2];
                    var distance = dr * dr + 2 * dg * dg + db * db;
                    if (distance < bestDistance) { bestDistance = distance; best = p; }
                }
                indices |= (uint)best << (i * 2);
            }

            output[destination++] = (byte)color0;
            output[destination++] = (byte)(color0 >> 8);
            output[destination++] = (byte)color1;
            output[destination++] = (byte)(color1 >> 8);
            output[destination++] = (byte)indices;
            output[destination++] = (byte)(indices >> 8);
            output[destination++] = (byte)(indices >> 16);
            output[destination++] = (byte)(indices >> 24);
        }
        return output;
    }

    private static byte[] DecodeBc1(ReadOnlySpan<byte> encoded, int width, int height)
    {
        var blockWidth = Math.Max(1, (width + 3) / 4);
        var blockHeight = Math.Max(1, (height + 3) / 4);
        var required = blockWidth * blockHeight * 8;
        if (encoded.Length < required)
            throw new InvalidDataException("DXT1 mipmap data is truncated.");
        var output = new byte[width * height * 4];
        var palette = new int[12];
        var offset = 0;
        for (var by = 0; by < blockHeight; by++)
        for (var bx = 0; bx < blockWidth; bx++)
        {
            var color0 = (ushort)(encoded[offset] | (encoded[offset + 1] << 8));
            var color1 = (ushort)(encoded[offset + 2] | (encoded[offset + 3] << 8));
            var indices = (uint)(encoded[offset + 4] | (encoded[offset + 5] << 8) | (encoded[offset + 6] << 16) | (encoded[offset + 7] << 24));
            offset += 8;
            Decode565(color0, palette, 0);
            Decode565(color1, palette, 3);
            if (color0 > color1)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    palette[6 + channel] = (2 * palette[channel] + palette[3 + channel]) / 3;
                    palette[9 + channel] = (palette[channel] + 2 * palette[3 + channel]) / 3;
                }
            }
            else
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    palette[6 + channel] = (palette[channel] + palette[3 + channel]) / 2;
                    palette[9 + channel] = 0;
                }
            }

            for (var py = 0; py < 4; py++)
            for (var px = 0; px < 4; px++)
            {
                var x = bx * 4 + px;
                var y = by * 4 + py;
                if (x >= width || y >= height) continue;
                var index = (int)((indices >> ((py * 4 + px) * 2)) & 3);
                var destination = (y * width + x) * 4;
                output[destination] = (byte)palette[index * 3 + 2];
                output[destination + 1] = (byte)palette[index * 3 + 1];
                output[destination + 2] = (byte)palette[index * 3];
                output[destination + 3] = (byte)(color0 <= color1 && index == 3 ? 0 : 255);
            }
        }
        return output;
    }

    private static byte[] ScaleCropBgra(byte[] source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        var sourceRatio = sourceWidth / (double)sourceHeight;
        var targetRatio = targetWidth / (double)targetHeight;
        var cropX = 0.0;
        var cropY = 0.0;
        var cropWidth = (double)sourceWidth;
        var cropHeight = (double)sourceHeight;
        if (sourceRatio > targetRatio)
        {
            cropWidth = sourceHeight * targetRatio;
            cropX = (sourceWidth - cropWidth) / 2;
        }
        else
        {
            cropHeight = sourceWidth / targetRatio;
            cropY = (sourceHeight - cropHeight) / 2;
        }

        var result = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < targetHeight; y++)
        for (var x = 0; x < targetWidth; x++)
        {
            var sourceX = Math.Clamp((int)(cropX + (x + 0.5) * cropWidth / targetWidth), 0, sourceWidth - 1);
            var sourceY = Math.Clamp((int)(cropY + (y + 0.5) * cropHeight / targetHeight), 0, sourceHeight - 1);
            Buffer.BlockCopy(source, (sourceY * sourceWidth + sourceX) * 4, result, (y * targetWidth + x) * 4, 4);
        }
        return result;
    }

    private static ushort To565(int r, int g, int b) =>
        (ushort)(((r * 31 + 127) / 255 << 11) | ((g * 63 + 127) / 255 << 5) | ((b * 31 + 127) / 255));

    private static void Decode565(ushort value, int[] palette, int offset)
    {
        palette[offset] = ((value >> 11) & 31) * 255 / 31;
        palette[offset + 1] = ((value >> 5) & 63) * 255 / 63;
        palette[offset + 2] = (value & 31) * 255 / 31;
    }
}
