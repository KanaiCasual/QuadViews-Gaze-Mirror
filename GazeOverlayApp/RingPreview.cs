using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GazeOverlay;

public enum PreviewBackground { Cockpit, Terrain, Sky, Black }

/// <summary>
/// CPU port of the layer's gaze pixel shader, so the look can be tuned without the game running. It follows the HLSL in
/// OpenXR-Layer-OBSMirror/dx11mirror.cpp (gaze_ps_code); keep the two in sync when the shader changes.
/// </summary>
public static class RingPreview
{
    // Pixels per "image height" unit. About what a cropped 1080p stream of a high-resolution VR mirror shows.
    private const double PixelsPerImageHeight = 2000;

    private readonly record struct Shade(double R, double G, double B, double A);

    public static WriteableBitmap Render(IReadOnlyDictionary<string, string> values, PreviewBackground background, bool moving, int size)
    {
        double Get(string key) => Settings.ParseDouble(values.GetValueOrDefault(key), Settings.ParseDouble(Settings.All.First(d => d.Key == key).Default, 0));

        var style = values.GetValueOrDefault("style") ?? "ghost";
        var screenBlend = (values.GetValueOrDefault("blend") ?? "screen") != "normal" && style != "spotlight";
        var (cr, cg, cb) = Settings.ParseColor(values.GetValueOrDefault("color"));
        var color = (R: ToLinear(cr), G: ToLinear(cg), B: ToLinear(cb));

        var p = new Params(
            Radius: Get("radius"), Thickness: Get("thickness"), Feather: Get("feather"), Glow: Math.Max(Get("glow"), 1e-4),
            GlowStrength: Get("glow_strength"), Opacity: Get("opacity"), FillOpacity: Get("fill_opacity"),
            ShadowOpacity: Get("shadow_opacity"), TailOpacity: Get("tail_opacity"), Solidity: Get("solidity"));

        // A representative "eyes moving up and to the right" pose: the tail trails down-left.
        var tailLength = Math.Min(Get("tail_max"), 0.11);
        var (tipX, tipY) = moving && style == "ghost" ? (-tailLength * 0.78, tailLength * 0.62) : (0.0, 0.0);
        var centerX = moving ? size * 0.62 : size * 0.5;
        var centerY = moving ? size * 0.38 : size * 0.5;

        var pixels = new byte[size * size * 4];
        var enabled = (values.GetValueOrDefault("enabled") ?? "1") != "0";
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var (br, bg, bb) = Background(background, x, y);
                var shade = enabled
                    ? ShadePixel(style, p, color, (x + 0.5 - centerX) / PixelsPerImageHeight, (y + 0.5 - centerY) / PixelsPerImageHeight, tipX, tipY)
                    : default;

                double r = br, g = bg, b = bb;
                if (shade.A > 0.0005)
                {
                    if (screenBlend)
                    {
                        // src * (1 - dest) + dest * (1 - alpha * solidity), with premultiplied src.
                        var keep = 1 - shade.A * p.Solidity;
                        r = shade.R * shade.A * (1 - br) + br * keep;
                        g = shade.G * shade.A * (1 - bg) + bg * keep;
                        b = shade.B * shade.A * (1 - bb) + bb * keep;
                    }
                    else
                    {
                        r = shade.R * shade.A + br * (1 - shade.A);
                        g = shade.G * shade.A + bg * (1 - shade.A);
                        b = shade.B * shade.A + bb * (1 - shade.A);
                    }
                }

                var i = (y * size + x) * 4;
                pixels[i] = ToSrgb(b);
                pixels[i + 1] = ToSrgb(g);
                pixels[i + 2] = ToSrgb(r);
                pixels[i + 3] = 255;
            }
        }

        var bitmap = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed record Params(double Radius, double Thickness, double Feather, double Glow, double GlowStrength,
                                 double Opacity, double FillOpacity, double ShadowOpacity, double TailOpacity, double Solidity);

    private static Shade ShadePixel(string style, Params p, (double R, double G, double B) color, double px, double py, double tipX, double tipY)
    {
        var dist = Math.Sqrt(px * px + py * py);
        var shadowWidth = p.Feather * 3 + 0.0015;
        switch (style)
        {
            case "ring":
            {
                var half = p.Thickness * 0.5;
                var soft = Math.Max(Math.Min(p.Feather, p.Thickness) * 0.5, 0.00002);
                var edge = Math.Abs(dist - p.Radius);
                var ring = 1 - Smoothstep(half - soft, half + soft, edge);
                var disc = 1 - Smoothstep(p.Radius, p.Radius + Math.Max(p.Feather, 1e-5), dist);
                var shadow = 1 - Smoothstep(half, half + soft + shadowWidth, edge);
                return WithShadow(color, Math.Max(ring * p.Opacity, disc * p.FillOpacity), shadow * p.ShadowOpacity);
            }
            case "glow":
            {
                var x = dist / Math.Max(p.Radius, 1e-4);
                return new Shade(color.R, color.G, color.B, p.Opacity * Math.Exp(-2 * x * x));
            }
            case "dot":
            {
                var core = 1 - Smoothstep(p.Radius, p.Radius + Math.Max(p.Feather, 1e-5), dist);
                var shadow = 1 - Smoothstep(p.Radius, p.Radius + p.Feather + shadowWidth, dist);
                return WithShadow(color, core * p.Opacity, shadow * p.ShadowOpacity);
            }
            case "spotlight":
            {
                var outside = Smoothstep(p.Radius, p.Radius + Math.Max(p.Feather, p.Radius), dist);
                return new Shade(0, 0, 0, p.Opacity * outside);
            }
            case "bubble":
            case "solid":
            {
                var outer = 1 - Smoothstep(p.Radius, p.Radius + Math.Max(p.Feather, 1e-5), dist);
                var inner = p.Opacity;
                if (style == "bubble")
                {
                    var t = Math.Clamp(dist / p.Radius, 0, 1);
                    inner = Lerp(p.FillOpacity, p.Opacity, t * t * t);
                }
                return new Shade(color.R, color.G, color.B, inner * outer);
            }
            case "heatmap":
            {
                var x = dist / Math.Max(p.Radius, 1e-4);
                var heat = Math.Clamp(Math.Exp(-2 * x * x), 0, 1);
                var (r, g, b) = heat < 0.33 ? Mix((0, 0.25, 1), (0, 1, 0.3), heat / 0.33)
                              : heat < 0.66 ? Mix((0, 1, 0.3), (1, 0.85, 0), (heat - 0.33) / 0.33)
                                            : Mix((1, 0.85, 0), (1, 0.08, 0), (heat - 0.66) / 0.34);
                return new Shade(r, g, b, p.Opacity * Smoothstep(0.03, 0.3, heat));
            }
            default:
                return Ghost(p, color, px, py, tipX, tipY);
        }
    }

    private static Shade Ghost(Params p, (double R, double G, double B) color, double px, double py, double tipX, double tipY)
    {
        var radius = p.Radius;
        var tipRadius = radius * 0.03;
        var tailLength = Math.Sqrt(tipX * tipX + tipY * tipY);
        var (dirX, dirY) = tailLength > 1e-5 ? (tipX / tailLength, tipY / tailLength) : (1.0, 0.0);
        var a = px * dirX + py * dirY;
        var b = -px * dirY + py * dirX;

        var deform = Math.Clamp(tailLength / (2.5 * radius), 0, 1);
        var squash = 1 - 0.12 * deform;
        var qx = a > 0 ? a / (1 + 0.35 * deform) : a / (1 - 0.08 * deform);
        var qy = b / squash;
        var dHead = Math.Sqrt(qx * qx + qy * qy) - radius;

        double dDrop = dHead, along = 0, stretch = 0;
        var headRadius = radius * squash;
        if (tailLength > headRadius - tipRadius + 0.0005)
        {
            var dTail = TaperedCapsule(px, py, tipX, tipY, headRadius, tipRadius);
            var k = radius * 0.35;
            var h = Math.Clamp(0.5 + 0.5 * (dTail - dHead) / k, 0, 1);
            dDrop = Lerp(dTail, dHead, h) - k * h * (1 - h);
            along = Math.Clamp((a - radius) / Math.Max(tailLength - radius, 1e-4), 0, 1);
            stretch = Math.Clamp((tailLength - radius) / radius, 0, 1);
        }

        var facing = -a / Math.Max(Math.Sqrt(px * px + py * py), 1e-5);
        var dissolve = Math.Clamp((tailLength - 0.5 * radius) / (1.5 * radius), 0, 1);
        var stroke = Lerp(1, Smoothstep(-0.8, 0.45, facing), dissolve);

        var half = p.Thickness * 0.5;
        var soft = Math.Max(Math.Min(p.Feather, p.Thickness) * 0.5, 0.00002);
        var ring = (1 - Smoothstep(half - soft, half + soft, Math.Abs(dHead))) * stroke;
        var ringGlow = Math.Exp(-Math.Abs(dHead) / p.Glow) * stroke;

        var outsideHead = Smoothstep(-half, half, dHead);
        var behind = Smoothstep(-0.35, 0.45, -facing);
        var tailFade = Math.Pow(1 - along, 0.8) * stretch * outsideHead * behind;
        var tailSoft = Math.Max(p.Glow, 0.006);
        var tailBody = (1 - Smoothstep(-tailSoft, tailSoft * 0.5, dDrop)) * tailFade * p.TailOpacity;
        var tailGlow = Math.Exp(-Math.Max(dDrop, 0) / tailSoft) * tailFade;

        var inside = (1 - Smoothstep(-Math.Max(p.Feather, 1e-5), Math.Max(p.Feather, 1e-5), dHead)) * p.FillOpacity;
        var alpha = Math.Max(Math.Max(ring, tailBody) * p.Opacity, Math.Max(ringGlow * p.GlowStrength, tailGlow * 0.35) * p.Opacity);
        return new Shade(color.R, color.G, color.B, Math.Max(alpha, inside));
    }

    private static double TaperedCapsule(double px, double py, double bx, double by, double ra, double rb)
    {
        var h = bx * bx + by * by;
        var qx = Math.Abs((px * by - py * bx) / h);
        var qy = (px * bx + py * by) / h;
        var diff = ra - rb;
        var cx = Math.Sqrt(Math.Max(h - diff * diff, 0));
        var cy = diff;
        var k = cx * qy - cy * qx;
        var m = cx * qx + cy * qy;
        var n = qx * qx + qy * qy;
        if (k < 0) return Math.Sqrt(h * n) - ra;
        if (k > cx) return Math.Sqrt(h * (n + 1 - 2 * qy)) - rb;
        return m - ra;
    }

    private static Shade WithShadow((double R, double G, double B) color, double foreground, double shadow)
    {
        var alpha = foreground + shadow * (1 - foreground);
        var scale = foreground / Math.Max(alpha, 1e-4);
        return new Shade(color.R * scale, color.G * scale, color.B * scale, alpha);
    }

    /// <summary>Procedural stand-ins for typical flight-sim backdrops, in linear light.</summary>
    private static (double R, double G, double B) Background(PreviewBackground kind, int x, int y)
    {
        switch (kind)
        {
            case PreviewBackground.Black:
                return (0, 0, 0);
            case PreviewBackground.Sky:
                return (0.13, 0.30, 0.70);
            case PreviewBackground.Terrain:
            {
                var n = 0.5 + 0.5 * Math.Sin(x * 0.21 + Math.Sin(y * 0.13) * 3.0) * Math.Sin(y * 0.17 + x * 0.05);
                return (0.30 + 0.30 * n, 0.22 + 0.22 * n, 0.13 + 0.15 * n);
            }
            default:
            {
                // Dark cockpit panel with a few "instrument" highlights.
                var n = 0.5 + 0.5 * Math.Sin(x * 0.35) * Math.Sin(y * 0.27);
                var gauge = Math.Exp(-(Math.Pow((x % 110) - 55, 2) + Math.Pow((y % 110) - 55, 2)) / 900.0) * 0.06;
                return (0.010 + 0.03 * n + gauge, 0.012 + 0.03 * n + gauge, 0.016 + 0.035 * n + gauge);
            }
        }
    }

    private static double Smoothstep(double a, double b, double x)
    {
        if (b <= a) return x < a ? 0 : 1;
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static (double, double, double) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));

    private static double ToLinear(byte c) => Math.Pow(c / 255.0, 2.2);

    private static byte ToSrgb(double linear) => (byte)Math.Round(Math.Pow(Math.Clamp(linear, 0, 1), 1 / 2.2) * 255);
}
