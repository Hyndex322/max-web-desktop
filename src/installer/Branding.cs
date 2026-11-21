using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;

namespace MaxSetup
{
    internal static class Branding
    {
        public static Bitmap CreateLogo(int size)
        {
            Bitmap loaded = TryLoadResourceLogo(size);
            if (loaded != null)
                return loaded;

            return DrawFallbackLogo(size);
        }

        private static Bitmap TryLoadResourceLogo(int size)
        {
            try
            {
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("logo.png"))
                {
                    if (stream == null) return null;

                    using (var source = Image.FromStream(stream))
                    {
                        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.SmoothingMode = SmoothingMode.HighQuality;
                            g.Clear(Color.Transparent);
                            g.DrawImage(source, new Rectangle(0, 0, size, size));
                        }
                        return bmp;
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap DrawFallbackLogo(int size)
        {
            var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                float pad = size * 0.035f;
                var rect = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
                float radius = size * 0.225f;

                using (GraphicsPath path = RoundedRect(rect, radius))
                {
                    using (var brush = new LinearGradientBrush(
                        new RectangleF(0, 0, size, size),
                        Color.FromArgb(255, 150, 122, 255),
                        Color.FromArgb(255, 72, 40, 200),
                        55f))
                    {
                        var blend = new ColorBlend(3);
                        blend.Colors = new[]
                        {
                            Color.FromArgb(255, 150, 122, 255),
                            Color.FromArgb(255, 108, 80, 245),
                            Color.FromArgb(255, 72, 40, 200)
                        };
                        blend.Positions = new[] { 0f, 0.55f, 1f };
                        brush.InterpolationColors = blend;
                        g.FillPath(brush, path);
                    }

                    using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, size * 0.012f)))
                        g.DrawPath(pen, path);
                }

                string text = size >= 40 ? "MAX" : "M";
                float fontSize = size >= 40 ? size * 0.30f : size * 0.52f;

                using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat())
                using (var textBrush = new SolidBrush(Color.White))
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString(text, font, textBrush, new RectangleF(0, size * 0.02f, size, size), format);
                }
            }
            return bmp;
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
