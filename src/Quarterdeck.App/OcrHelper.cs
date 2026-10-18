using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using Quarterdeck.Core.Agent;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Quarterdeck.App;

/// <summary>
/// The built-in ocr_read helper: the exact text in a region of the remote screen, read by Windows'
/// own OCR engine (Windows 10 2004 and later, no download). Models misread small and thin text;
/// in testing a model read a thin-font chess move list right 80-97% of the time on its own.
/// </summary>
internal static class OcrHelper
{
    public static AgentHelper Definition { get; } = new()
    {
        Name = "ocr_read",
        Screenshot = true,
        Description = "The text in a region of the screen, read by Windows OCR, line by line with each line's position on " +
                      "your 0-1000 grid. Use it for messages, labels, numbers and codes you must quote exactly, and for tables " +
                      "and lists. On very thin or decorative fonts it can slip (l for 1): zoom and compare when it matters.",
        Parameters = """
            {"type":"object","required":["x","y","width","height"],"properties":{
              "x":{"type":"integer","description":"Left edge, 0-1000 across the screen."},
              "y":{"type":"integer","description":"Top edge, 0-1000 down the screen."},
              "width":{"type":"integer","description":"Width on the grid, 10-1000."},
              "height":{"type":"integer","description":"Height on the grid, 10-1000."}}}
            """,
        Run = RunAsync
    };

    private static int Int(JsonObject a, string k, int fallback) =>
        double.TryParse(a[k]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Round(v) : fallback;

    private static async Task<string> RunAsync(JsonObject args, ScreenShot? shot, CancellationToken token)
    {
        if (shot is null)
        {
            return "error: no screen to read.";
        }

        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine is null)
        {
            return "error: Windows has no OCR language installed (Settings > Time & language > Language).";
        }

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(shot.Jpeg.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;

        int gx = Math.Clamp(Int(args, "x", 0), 0, 1000), gy = Math.Clamp(Int(args, "y", 0), 0, 1000);
        int gw = Math.Clamp(Int(args, "width", 1000), 10, 1000), gh = Math.Clamp(Int(args, "height", 1000), 10, 1000);
        int x = gx * (w - 1) / 1000, y = gy * (h - 1) / 1000;
        int rw = Math.Max(8, Math.Min(w - x, gw * w / 1000)), rh = Math.Max(8, Math.Min(h - y, gh * h / 1000));

        // Enlarge small regions (thin text reads better bigger), within the engine's size limit.
        var scale = Math.Clamp(Math.Min(OcrEngine.MaxImageDimension / (double)Math.Max(rw, rh), 3.0), 0.25, 3.0);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, w * scale),
            ScaledHeight = (uint)Math.Max(1, h * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (uint)(x * scale), Y = (uint)(y * scale), Width = (uint)(rw * scale), Height = (uint)(rh * scale) }
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);

        // Windows reads a table column by column (every number, then every name); rebuild the rows
        // from where each word sits: words whose centres share a height form one line, left to right.
        var words = result.Lines.SelectMany(l => l.Words)
            .Select(wd => (wd.Text, X: wd.BoundingRect.X, Cy: wd.BoundingRect.Y + wd.BoundingRect.Height / 2, H: wd.BoundingRect.Height))
            .OrderBy(wd => wd.Cy).ToList();
        var rows = new List<List<(string Text, double X, double Cy, double H)>>();
        foreach (var wd in words)
        {
            var row = rows.LastOrDefault();
            if (row is not null && Math.Abs(row.Average(r => r.Cy) - wd.Cy) < Math.Max(4, wd.H * 0.5))
            {
                row.Add(wd);
            }
            else
            {
                rows.Add([wd]);
            }
        }

        // Back to the agent's grid: the region's origin plus the position inside the scaled crop.
        var lines = new JsonArray();
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(r => r.X).ToList();
            lines.Add(new JsonObject
            {
                ["text"] = string.Join(" ", ordered.Select(r => r.Text)),
                ["x"] = (int)Math.Round((x + ordered[0].X / scale) * 1000 / (w - 1)),
                ["y"] = (int)Math.Round((y + ordered[0].Cy / scale) * 1000 / (h - 1))
            });
        }

        return new JsonObject
        {
            ["text"] = string.Join("\n", lines.Select(l => l!["text"]!.ToString())),
            ["lines"] = lines,
            ["language"] = engine.RecognizerLanguage.LanguageTag,
            ["note"] = "OCR, not a promise: on very thin fonts it can mistake l for 1 and the like; check what matters."
        }.ToJsonString();
    }
}
