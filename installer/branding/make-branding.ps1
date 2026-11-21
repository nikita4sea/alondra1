# Renders the Alondra1 setup wizard images from bird-1024.png (the transparent logo cutout).
# Output: wizard-<w>.bmp (Welcome/Finish side panel) and small-<s>.bmp (page header), one per Windows DPI scale,
# plus docs/images/alondra1-banner.png for the README.
# Run with Windows PowerShell:  powershell -NoProfile -ExecutionPolicy Bypass -File installer\branding\make-branding.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

public static class Alondra1Branding
{
    static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

    static Graphics Prepare(Bitmap bmp)
    {
        Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        return g;
    }

    // Side panel shown on the Welcome and Finish pages (base size 164 x 314).
    public static void Wizard(string birdPath, string outPath, int w, int h)
    {
        float s = w / 164f;
        using (Bitmap bird = new Bitmap(birdPath))
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        using (Graphics g = Prepare(bmp))
        {
            using (LinearGradientBrush bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), C(0xFBF9FE), C(0xD3D1EC), 90f))
                g.FillRectangle(bg, 0, 0, w, h);

            // Soft peach glow behind the bird.
            using (GraphicsPath glow = new GraphicsPath())
            {
                float r = 70 * s, cx = w / 2f, cy = 92 * s;
                glow.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
                using (PathGradientBrush pb = new PathGradientBrush(glow))
                {
                    pb.CenterColor = Color.FromArgb(150, C(0xF9DCCE));
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, C(0xF9DCCE)) };
                    g.FillPath(pb, glow);
                }
            }

            // Lavender wave near the bottom, echoing the wings.
            using (GraphicsPath wave = new GraphicsPath())
            {
                wave.AddBezier(0, h * 0.80f, w * 0.35f, h * 0.72f, w * 0.65f, h * 0.90f, w, h * 0.78f);
                wave.AddLine(w, h * 0.78f, w, h);
                wave.AddLine(w, h, 0, h);
                wave.CloseFigure();
                using (SolidBrush wb = new SolidBrush(Color.FromArgb(70, C(0x9397C4))))
                    g.FillPath(wb, wave);
            }
            using (GraphicsPath wave2 = new GraphicsPath())
            {
                wave2.AddBezier(0, h * 0.88f, w * 0.40f, h * 0.82f, w * 0.70f, h * 0.96f, w, h * 0.86f);
                wave2.AddLine(w, h * 0.86f, w, h);
                wave2.AddLine(w, h, 0, h);
                wave2.CloseFigure();
                using (SolidBrush wb = new SolidBrush(Color.FromArgb(90, C(0x6C6DB0))))
                    g.FillPath(wb, wave2);
            }

            float bs = 112 * s;
            g.DrawImage(bird, (w - bs) / 2f, 36 * s, bs, bs);

            StringFormat center = new StringFormat();
            center.Alignment = StringAlignment.Center;
            using (Font title = new Font("Segoe UI Semibold", 22 * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush tb = new SolidBrush(C(0x2A2940)))
                g.DrawString("Alondra1", title, tb, new RectangleF(0, 160 * s, w, 34 * s), center);
            using (Font tag = new Font("Segoe UI", 10.5f * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush mb = new SolidBrush(C(0x5F5E7A)))
                g.DrawString("Mail " + (char)0xB7 + " Calendar " + (char)0xB7 + " To-do", tag, mb, new RectangleF(0, 192 * s, w, 18 * s), center);
            using (Pen accent = new Pen(C(0xF1C2AA), 2 * s))
                g.DrawLine(accent, w / 2f - 14 * s, 216 * s, w / 2f + 14 * s, 216 * s);

            bmp.Save(outPath, ImageFormat.Bmp);
        }
    }

    // Header image on the inner pages (base size 55 x 55): the bird on white, matching the header panel.
    public static void Small(string birdPath, string outPath, int size)
    {
        using (Bitmap bird = new Bitmap(birdPath))
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format24bppRgb))
        using (Graphics g = Prepare(bmp))
        {
            g.Clear(Color.White);
            float pad = size * 0.04f;
            g.DrawImage(bird, pad, pad, size - 2 * pad, size - 2 * pad);
            bmp.Save(outPath, ImageFormat.Bmp);
        }
    }

    // README banner (1280 x 400 PNG): the bird, the wordmark, and the tagline on the same lavender ground as the wizard.
    public static void Banner(string birdPath, string outPath)
    {
        int w = 1280, h = 400;
        using (Bitmap bird = new Bitmap(birdPath))
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        using (Graphics g = Prepare(bmp))
        {
            using (LinearGradientBrush bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), C(0xFBF9FE), C(0xDCDAF0), 20f))
                g.FillRectangle(bg, 0, 0, w, h);
            using (GraphicsPath glow = new GraphicsPath())
            {
                float r = 230, cx = 300, cy = 190;
                glow.AddEllipse(cx - r, cy - r, 2 * r, 2 * r);
                using (PathGradientBrush pb = new PathGradientBrush(glow))
                {
                    pb.CenterColor = Color.FromArgb(150, C(0xF9DCCE));
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, C(0xF9DCCE)) };
                    g.FillPath(pb, glow);
                }
            }
            using (GraphicsPath wave = new GraphicsPath())
            {
                wave.AddBezier(0, h * 0.86f, w * 0.30f, h * 0.76f, w * 0.65f, h * 0.98f, w, h * 0.82f);
                wave.AddLine(w, h * 0.82f, w, h);
                wave.AddLine(w, h, 0, h);
                wave.CloseFigure();
                using (SolidBrush wb = new SolidBrush(Color.FromArgb(70, C(0x9397C4))))
                    g.FillPath(wb, wave);
            }
            g.DrawImage(bird, 110, 40, 300, 300);
            using (Font title = new Font("Segoe UI Semibold", 104, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush tb = new SolidBrush(C(0x2A2940)))
                g.DrawString("Alondra1", title, tb, 455, 88);
            using (Font tag = new Font("Segoe UI", 32, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush mb = new SolidBrush(C(0x5F5E7A)))
                g.DrawString("Local-first mail, calendar, and to-do for Windows", tag, mb, 466, 236);
            using (Pen accent = new Pen(C(0xF1C2AA), 5))
                g.DrawLine(accent, 470, 300, 560, 300);
            bmp.Save(outPath, ImageFormat.Png);
        }
    }
}
"@
$here = $PSScriptRoot
$bird = Join-Path $here "bird-1024.png"
$docsImages = Join-Path (Split-Path -Parent (Split-Path -Parent $here)) "docs\images"
[Alondra1Branding]::Banner($bird, (Join-Path $docsImages "alondra1-banner.png"))
$scales = 100, 125, 150, 175, 200, 225, 250
foreach ($p in $scales) {
    $w = [int][math]::Round(164 * $p / 100); $h = [int][math]::Round(314 * $p / 100); $sm = [int][math]::Round(55 * $p / 100)
    [Alondra1Branding]::Wizard($bird, (Join-Path $here "wizard-$w.bmp"), $w, $h)
    [Alondra1Branding]::Small($bird, (Join-Path $here "small-$sm.bmp"), $sm)
}
Get-ChildItem $here -Filter *.bmp | ForEach-Object { $_.Name }
