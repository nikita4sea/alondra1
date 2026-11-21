using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

// Generates Alondra1's built-in backgrounds. All images are original, procedurally drawn artwork made for Alondra1;
// they contain no third-party content. Run design/backgrounds/generate.ps1 to rebuild them.
public static class Alondra1Backgrounds
{
    const int W = 1920, H = 1200;

    public static void All(string outDir)
    {
        Directory.CreateDirectory(outDir);
        Save(SoftBlue(), outDir, "soft-blue");
        Save(GrayGeometric(), outDir, "gray-geometric");
        Save(Coast(), outDir, "coast");
        Save(Mountains(), outDir, "mountains");
        Save(Architecture(), outDir, "architecture");
        Save(WarmNeutral(), outDir, "warm-neutral");
        Save(DarkBlue(), outDir, "dark-blue");
        Save(Ocean(), outDir, "ocean");
    }

    static void Save(Bitmap bmp, string dir, string name)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var options = new EncoderParameters(1);
        options.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 84L);
        bmp.Save(Path.Combine(dir, name + ".jpg"), codec, options);
        bmp.Dispose();
    }

    sealed class Blob { public float X, Y, R; public Color Color; public Blob(float x, float y, float r, Color color) { X = x; Y = y; R = r; Color = color; } }

    static Color C(string hex, int alpha = 255) { var c = ColorTranslator.FromHtml(hex); return Color.FromArgb(alpha, c); }

    static Graphics G(Bitmap b)
    {
        var g = Graphics.FromImage(b);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        return g;
    }

    static void Vertical(Graphics g, Rectangle r, Color top, Color bottom)
    {
        using (var brush = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), top, bottom, LinearGradientMode.Vertical))
            g.FillRectangle(brush, r);
    }

    /// <summary>Soft blobs drawn at 1/8 size and scaled up, which gives a smooth, very wide blur.</summary>
    static Bitmap Blobs(Color from, Color to, float angle, IEnumerable<Blob> blobs)
    {
        const int s = 8;
        using (var small = new Bitmap(W / s, H / s))
        {
            using (var g = G(small))
            {
                using (var brush = new LinearGradientBrush(new Rectangle(0, 0, W / s, H / s), from, to, angle))
                    g.FillRectangle(brush, 0, 0, W / s, H / s);
                foreach (var blob in blobs)
                {
                    float x = blob.X, y = blob.Y, r = blob.R; var color = blob.Color;
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse((x - r) * W / s, (y - r) * W / s, 2 * r * W / s, 2 * r * W / s);
                        using (var brush = new PathGradientBrush(path) { CenterColor = color, SurroundColors = new[] { Color.FromArgb(0, color) } })
                            g.FillPath(brush, path);
                    }
                }
            }

            return Upscale(small);
        }
    }

    static Bitmap Upscale(Bitmap small)
    {
        var big = new Bitmap(W, H);
        using (var g = G(big))
        using (var attrs = new ImageAttributes())
        {
            attrs.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(small, new Rectangle(0, 0, W, H), 0, 0, small.Width, small.Height, GraphicsUnit.Pixel, attrs);
        }

        return big;
    }

    static Bitmap Soften(Bitmap sharp, int factor)
    {
        using (var small = new Bitmap(W / factor, H / factor))
        {
            using (var g = G(small))
                g.DrawImage(sharp, new Rectangle(0, 0, small.Width, small.Height));
            sharp.Dispose();
            return Upscale(small);
        }
    }

    static Bitmap SoftBlue() { return Blobs(C("#E3EFFB"), C("#F5F8FC"), 35f, new[]
    {
        new Blob(0.18f, 0.20f, 0.35f, C("#A9CBEE", 200)),
        new Blob(0.82f, 0.30f, 0.30f, C("#C9DDF4", 220)),
        new Blob(0.62f, 0.85f, 0.38f, C("#9FC2E8", 150)),
        new Blob(0.95f, 0.95f, 0.22f, C("#FFE6A3", 110)),
    }); }

    static Bitmap WarmNeutral() { return Blobs(C("#F4EEE6"), C("#EFE6DA"), 60f, new[]
    {
        new Blob(0.20f, 0.30f, 0.34f, C("#E8D5BD", 210)),
        new Blob(0.78f, 0.22f, 0.28f, C("#F5E3C4", 200)),
        new Blob(0.55f, 0.88f, 0.36f, C("#DCC8B4", 170)),
        new Blob(0.05f, 0.92f, 0.20f, C("#F2D9A6", 120)),
    }); }

    static Bitmap DarkBlue() { return Blobs(C("#0F1B2D"), C("#18304D"), 70f, new[]
    {
        new Blob(0.25f, 0.25f, 0.40f, C("#2B5A8D", 150)),
        new Blob(0.85f, 0.70f, 0.34f, C("#1F4D88", 170)),
        new Blob(0.60f, 0.10f, 0.20f, C("#FFD15C", 28)),
        new Blob(0.10f, 0.95f, 0.30f, C("#21466F", 140)),
    }); }

    static Bitmap GrayGeometric()
    {
        var bmp = new Bitmap(W, H);
        var random = new Random(7);
        using (var g = G(bmp))
        {
            Vertical(g, new Rectangle(0, 0, W, H), C("#F2F4F7"), C("#E9ECF0"));
            const int cell = 160;
            var shades = new[] { "#EEF1F4", "#E6EAEF", "#F5F7F9", "#E2E7EC", "#ECEFF3" };
            for (var row = -1; row < H / cell + 2; row++)
                for (var col = -1; col < W / cell + 2; col++)
                {
                    var x = col * cell + (row % 2 == 0 ? 0 : cell / 2);
                    var y = row * cell;
                    var a = new PointF(x, y);
                    var b = new PointF(x + cell, y);
                    var c = new PointF(x + cell / 2f, y + cell);
                    var d = new PointF(x - cell / 2f, y + cell);
                    using (var brush = new SolidBrush(C(shades[random.Next(shades.Length)])))
                        g.FillPolygon(brush, new[] { a, b, c });
                    using (var brush = new SolidBrush(C(shades[random.Next(shades.Length)])))
                        g.FillPolygon(brush, new[] { a, c, d });
                }

            using (var pen = new Pen(C("#FFFFFF", 90), 1.5f))
                for (var row = -1; row < H / cell + 2; row++)
                    for (var col = -1; col < W / cell + 2; col++)
                    {
                        var x = col * cell + (row % 2 == 0 ? 0 : cell / 2);
                        var y = row * cell;
                        g.DrawPolygon(pen, new[] { new PointF(x, y), new PointF(x + cell, y), new PointF(x + cell / 2f, y + cell) });
                    }
        }

        return bmp;
    }

    static Bitmap Coast()
    {
        var bmp = new Bitmap(W, H);
        var horizon = (int)(H * 0.52);
        using (var g = G(bmp))
        {
            Vertical(g, new Rectangle(0, 0, W, horizon), C("#C8D8E6"), C("#EEF1F2"));
            // Distant headland.
            using (var path = new GraphicsPath())
            {
                path.AddCurve(new[] { new PointF(1100, horizon), new PointF(1350, horizon - 55), new PointF(1600, horizon - 90), new PointF(1800, horizon - 70), new PointF(1960, horizon - 40) });
                path.AddLine(1960, horizon - 40, 1960, horizon + 2);
                path.AddLine(1960, horizon + 2, 1100, horizon + 2);
                using (var brush = new SolidBrush(C("#AFBFCA")))
                    g.FillPath(brush, path);
            }

            Vertical(g, new Rectangle(0, horizon, W, H - horizon), C("#A9BECB"), C("#CBD8DF"));
            // Soft swell lines.
            using (var pen = new Pen(C("#FFFFFF", 55), 2.2f))
                for (var i = 0; i < 9; i++)
                {
                    var y = horizon + 30 + i * i * 6 + i * 18;
                    g.DrawBezier(pen, -20, y, 600, y - 8, 1300, y + 10, 1960, y - 4);
                }

            // Sand.
            using (var sand = new GraphicsPath())
            {
                sand.AddBezier(-20, H * 0.80f, 500, H * 0.74f, 1200, H * 0.86f, 1960, H * 0.78f);
                sand.AddLine(1960, H * 0.78f, 1960, H + 20);
                sand.AddLine(1960, H + 20, -20, H + 20);
                using (var brush = new LinearGradientBrush(new Rectangle(0, (int)(H * 0.72), W, (int)(H * 0.3)), C("#E6DFD2"), C("#D9CFBF"), LinearGradientMode.Vertical))
                    g.FillPath(brush, sand);
            }

            using (var foam = new Pen(C("#FFFFFF", 150), 4f))
                g.DrawBezier(foam, -20, H * 0.80f, 500, H * 0.74f, 1200, H * 0.86f, 1960, H * 0.78f);
        }

        return Soften(bmp, 3);
    }

    /// <summary>Open sea under a hazy sky: waves grow toward the viewer and a soft sun glint lies on the water.</summary>
    static Bitmap Ocean()
    {
        var bmp = new Bitmap(W, H);
        var random = new Random(23);
        var horizon = (int)(H * 0.40);
        const float sunX = W * 0.68f;
        using (var g = G(bmp))
        {
            // Sky, with a warm haze around the low sun.
            Vertical(g, new Rectangle(0, 0, W, horizon), C("#8DB8DD"), C("#E3EEF4"));
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(sunX - 520, horizon - 420, 1040, 640);
                using (var haze = new PathGradientBrush(path) { CenterColor = C("#FFF4DA", 170), SurroundColors = new[] { C("#FFF4DA", 0) } })
                    g.FillPath(haze, path);
            }

            // Long, thin clouds.
            foreach (var c in new[] { new[] { 180f, 110f, 760f, 46f }, new[] { 980f, 70f, 620f, 34f }, new[] { 520f, 230f, 900f, 40f }, new[] { 1380f, 260f, 520f, 30f } })
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(c[0], c[1], c[2], c[3]);
                    using (var cloud = new PathGradientBrush(path) { CenterColor = C("#FFFFFF", 150), SurroundColors = new[] { C("#FFFFFF", 0) } })
                        g.FillPath(cloud, path);
                }

            // Water: hazy at the horizon, deep blue in front.
            using (var water = new LinearGradientBrush(new Rectangle(0, horizon - 1, W, H - horizon + 2), C("#7FAACA"), C("#164E7A"), LinearGradientMode.Vertical))
                g.FillRectangle(water, 0, horizon, W, H - horizon);
            using (var mist = new LinearGradientBrush(new Rectangle(0, horizon - 1, W, 92), C("#E3EEF4", 200), C("#E3EEF4", 0), LinearGradientMode.Vertical))
                g.FillRectangle(mist, 0, horizon, W, 90);

            // Waves: rows get further apart, longer, and thicker toward the viewer.
            for (var row = 0; row < 70; row++)
            {
                var depth = row / 69f;
                var y = horizon + 6 + (H - horizon) * depth * depth;
                var length = 40 + 260 * depth;
                var width = 0.8f + 3.2f * depth;
                for (var i = 0; i < 6 + (int)(10 * (1 - depth)); i++)
                {
                    var x = (float)random.NextDouble() * (W + length) - length;
                    var dy = (float)(random.NextDouble() - 0.5) * 8 * depth;
                    using (var trough = new Pen(C("#0E3A5E", 70), width * 1.3f))
                        g.DrawBezier(trough, x, y + dy + width * 2, x + length * 0.3f, y + dy + width * 4, x + length * 0.7f, y + dy + width * 4, x + length, y + dy + width * 2);
                    using (var crest = new Pen(C("#E8F3FA", 40 + (int)(50 * depth)), width))
                        g.DrawBezier(crest, x, y + dy, x + length * 0.3f, y + dy - width * 3, x + length * 0.7f, y + dy - width * 3, x + length, y + dy);
                }
            }

            // Sun glint: short bright dashes that widen and fade toward the viewer.
            for (var i = 0; i < 260; i++)
            {
                var depth = (float)Math.Pow(random.NextDouble(), 1.6);
                var y = horizon + 4 + (H - horizon) * depth;
                var spread = 60 + 260 * depth;
                var x = sunX + (float)((random.NextDouble() - 0.5) * 2 * spread * (0.4 + random.NextDouble() * 0.6));
                var length = 10 + 70 * depth * (float)random.NextDouble();
                using (var glint = new Pen(C("#FFF6DF", (int)(200 * (1 - depth * 0.8))), 1.5f + 3f * depth))
                    g.DrawLine(glint, x - length / 2, y, x + length / 2, y);
            }
        }

        return Soften(bmp, 3);
    }

    static Bitmap Mountains()
    {
        var bmp = new Bitmap(W, H);
        var random = new Random(11);
        using (var g = G(bmp))
        {
            Vertical(g, new Rectangle(0, 0, W, H), C("#E3E9EF"), C("#F3F4F5"));
            var colors = new[] { "#CBD5DE", "#B7C3CE", "#A0AFBD", "#8A9BAB", "#748797" };
            var baselines = new[] { 0.42, 0.52, 0.62, 0.72, 0.83 };
            for (var layer = 0; layer < colors.Length; layer++)
            {
                var color = colors[layer]; var baseline = baselines[layer];
                var points = new List<PointF> { new PointF(-10, H + 10) };
                var y = H * (float)baseline;
                for (var x = -10f; x <= W + 10; x += 40)
                {
                    y += (float)(random.NextDouble() - 0.5) * 70;
                    y = Math.Max(H * (float)baseline - 160, Math.Min(H * (float)baseline + 60, y));
                    points.Add(new PointF(x, y));
                }

                points.Add(new PointF(W + 10, H + 10));
                using (var brush = new SolidBrush(C(color)))
                    g.FillPolygon(brush, points.ToArray());
                // Mist at the foot of each ridge.
                var mistTop = (int)(H * baseline);
                using (var mist = new LinearGradientBrush(new Rectangle(0, mistTop - 1, W, 162), C("#F2F4F6", 0), C("#F2F4F6", 170), LinearGradientMode.Vertical))
                    g.FillRectangle(mist, 0, mistTop, W, 160);
            }
        }

        return Soften(bmp, 4);
    }

    static Bitmap Architecture()
    {
        var bmp = new Bitmap(W, H);
        using (var g = G(bmp))
        {
            Vertical(g, new Rectangle(0, 0, W, H), C("#EEE9E2"), C("#E4DDD3"));
            const int arch = 240, gap = 80, top = 330, bottom = 1060;
            for (var x = 60; x < W; x += arch + gap)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddArc(x, top, arch, arch, 180, 180);
                    path.AddLine(x + arch, top + arch / 2, x + arch, bottom);
                    path.AddLine(x + arch, bottom, x, bottom);
                    path.CloseFigure();
                    using (var shade = new LinearGradientBrush(new Rectangle(x, top, arch, bottom - top), C("#D8CFC4"), C("#CFC5B8"), LinearGradientMode.Vertical))
                        g.FillPath(shade, path);
                    // Light falling into the opening.
                    using (var light = new LinearGradientBrush(new Rectangle(x, top, arch, bottom - top), C("#F7F3EC", 140), C("#F7F3EC", 0), 60f))
                        g.FillPath(light, path);
                }
            }

            using (var floor = new SolidBrush(C("#DDD5CA")))
                g.FillRectangle(floor, 0, bottom, W, H - bottom);
            using (var line = new Pen(C("#CDBFAE", 120), 2f))
                g.DrawLine(line, 0, bottom, W, bottom);
        }

        return Soften(bmp, 2);
    }
}
