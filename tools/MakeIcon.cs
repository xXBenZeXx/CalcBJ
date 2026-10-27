using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

// Writes a multi-resolution PNG-compressed .ico: green felt tile, white card, black spade.
// With "--web <folder>" it writes the PNG icons of the web app instead, and with "--mac <file.png>" the 1024 px
// source of the desktop app's icons (the tile on Apple's icon grid: 824 px with a 100 px margin).
static class MakeIcon
{
    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Bitmap Draw(int s) { return Draw(s, false); }

    /// <summary>`fullBleed` fills the whole square (no rounded corners or transparency) and shrinks the card into the
    /// central safe zone, for icons the system crops into its own shape (Android maskable, iOS home screen).</summary>
    static Bitmap Draw(int s, bool fullBleed)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            var tile = fullBleed ? new RectangleF(0, 0, s, s) : new RectangleF(0.5f, 0.5f, s - 1f, s - 1f);
            using (var b = new LinearGradientBrush(tile, Color.FromArgb(0x2B, 0xB0, 0x77), Color.FromArgb(0x14, 0x6B, 0x46), 90f))
            {
                if (fullBleed) g.FillRectangle(b, tile);
                else using (var path = Rounded(tile, s * 0.2f)) g.FillPath(b, path);
            }
            g.TranslateTransform(s * 0.52f, s * 0.53f);
            if (fullBleed) g.ScaleTransform(0.72f, 0.72f);
            g.RotateTransform(-10f);
            var card = new RectangleF(-s * 0.27f, -s * 0.36f, s * 0.54f, s * 0.72f);
            using (var shadow = Rounded(new RectangleF(card.X + s * 0.03f, card.Y + s * 0.03f, card.Width, card.Height), s * 0.07f))
            using (var sb = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                g.FillPath(sb, shadow);
            using (var cp = Rounded(card, s * 0.07f))
                g.FillPath(Brushes.White, cp);
            float fs = s * 0.42f;
            using (var f = new Font("Segoe UI Symbol", fs, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                var fmt = new StringFormat();
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                using (var tb = new SolidBrush(Color.FromArgb(0x1C, 0x28, 0x33)))
                    g.DrawString("♠", f, tb, new RectangleF(card.X, card.Y + s * 0.02f, card.Width, card.Height), fmt);
            }
        }
        return bmp;
    }

    static void Png(Bitmap b, string path)
    {
        using (b) b.Save(path, ImageFormat.Png);
    }

    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--web")
        {
            Directory.CreateDirectory(args[1]);
            Png(Draw(32), Path.Combine(args[1], "favicon-32.png"));
            Png(Draw(192), Path.Combine(args[1], "icon-192.png"));
            Png(Draw(512), Path.Combine(args[1], "icon-512.png"));
            Png(Draw(512, true), Path.Combine(args[1], "icon-maskable-512.png"));
            Png(Draw(180, true), Path.Combine(args[1], "apple-touch-icon.png"));
            return;
        }
        if (args.Length == 2 && args[0] == "--mac")
        {
            using (var tile = Draw(824))
            using (var bmp = new Bitmap(1024, 1024, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.DrawImage(tile, 100, 100, 824, 824);
                }
                bmp.Save(args[1], ImageFormat.Png);
            }
            return;
        }
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (int s in sizes)
            using (var b = Draw(s))
            using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
        using (var fs = new FileStream(args[0], FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
        using (var b = Draw(256)) b.Save(Path.ChangeExtension(args[0], ".png"), ImageFormat.Png);
    }
}
