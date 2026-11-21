using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconTools
{
    private static readonly int[] Sizes = { 256, 128, 64, 48, 32, 24, 16 };

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 4 && args[0] == "from-image")
            {
                FromImage(args[1], args[2], args[3]);
                return 0;
            }

            if (args.Length >= 3 && args[0] == "from-ico")
            {
                FromIco(args[1], args[2]);
                return 0;
            }

            Console.Error.WriteLine(
                "Usage: IconTools from-image <input> <ico> <png> | from-ico <ico> <png>");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static void FromImage(string input, string icoOutput, string pngOutput)
    {
        using (var source = new Bitmap(input))
        {
            int side = Math.Min(source.Width, source.Height);
            int sx = (source.Width - side) / 2;
            int sy = (source.Height - side) / 2;
            var crop = new Rectangle(sx, sy, side, side);

            var frames = new List<byte[]>();

            foreach (int size in Sizes)
            {
                using (Bitmap bmp = Resize(source, crop, size))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    frames.Add(ms.ToArray());
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(icoOutput)));
            using (var fs = new FileStream(icoOutput, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((short)0);
                bw.Write((short)1);
                bw.Write((short)Sizes.Length);

                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    int s = Sizes[i];
                    bw.Write((byte)(s >= 256 ? 0 : s));
                    bw.Write((byte)(s >= 256 ? 0 : s));
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((short)1);
                    bw.Write((short)32);
                    bw.Write(frames[i].Length);
                    bw.Write(offset);
                    offset += frames[i].Length;
                }

                foreach (byte[] frame in frames)
                    bw.Write(frame);
            }

            if (!string.IsNullOrEmpty(pngOutput))
            {
                using (Bitmap logo = Resize(source, crop, 256))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngOutput)));
                    logo.Save(pngOutput, ImageFormat.Png);
                }
            }
        }

        Console.WriteLine("OK");
    }

    private static Bitmap Resize(Bitmap source, Rectangle crop, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.Clear(Color.Transparent);
            g.DrawImage(source, new Rectangle(0, 0, size, size), crop, GraphicsUnit.Pixel);
        }
        return bmp;
    }

    private static void FromIco(string icoInput, string pngOutput)
    {
        byte[] data = File.ReadAllBytes(icoInput);
        if (data.Length < 6 || data[0] != 0 || data[1] != 0 || data[2] != 1 || data[3] != 0)
            throw new InvalidDataException("Файл не является ICO.");

        int count = data[4] | (data[5] << 8);
        int bestOffset = -1;
        int bestLength = 0;
        int bestSize = -1;

        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            if (entry + 16 > data.Length) break;

            int width = data[entry] == 0 ? 256 : data[entry];
            int length = BitConverter.ToInt32(data, entry + 8);
            int offset = BitConverter.ToInt32(data, entry + 12);

            if (offset < 0 || length <= 0 || offset + length > data.Length) continue;

            if (width > bestSize)
            {
                bestSize = width;
                bestOffset = offset;
                bestLength = length;
            }
        }

        if (bestOffset < 0)
            throw new InvalidDataException("В ICO не найдено изображение.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngOutput)));

        // PNG-сжатый кадр можно просто скопировать.
        if (bestLength > 8 &&
            data[bestOffset] == 0x89 &&
            data[bestOffset + 1] == 0x50 &&
            data[bestOffset + 2] == 0x4E &&
            data[bestOffset + 3] == 0x47)
        {
            using (var fs = new FileStream(pngOutput, FileMode.Create, FileAccess.Write))
            {
                fs.Write(data, bestOffset, bestLength);
            }
            Console.WriteLine("OK");
            return;
        }

        // Иначе используем штатный загрузчик ICO.
        using (var icon = new Icon(icoInput, bestSize, bestSize))
        using (Bitmap bmp = icon.ToBitmap())
        {
            bmp.Save(pngOutput, ImageFormat.Png);
        }

        Console.WriteLine("OK");
    }
}
