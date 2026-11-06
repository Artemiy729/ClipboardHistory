using System.Drawing.Drawing2D;

namespace ClipboardHistory.Windows;

internal sealed class HistoryThumbnailCache : IDisposable
{
    private readonly Dictionary<Guid, Bitmap> images = [];
    private readonly Queue<Guid> order = new();
    private readonly HashSet<Guid> invalid = [];
    public Bitmap? Get(Entry entry)
    {
        if (images.TryGetValue(entry.Id, out var cached)) return cached;
        if (entry.Image is not byte[] data || invalid.Contains(entry.Id)) return null;
        try
        {
            using var stream = new MemoryStream(data);
            using var source = Image.FromStream(stream);
            var thumbnail = Resize(source, 112);
            while (images.Count >= 128 && order.TryDequeue(out var oldest))
                if (images.Remove(oldest, out var removed)) removed.Dispose();
            images[entry.Id] = thumbnail;
            order.Enqueue(entry.Id);
            return thumbnail;
        }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            invalid.Add(entry.Id);
            return null;
        }
    }

    internal static Bitmap Resize(Image source, int maximum)
    {
        var scale = Math.Min(1.0, (double)maximum / Math.Max(source.Width, source.Height));
        var bitmap = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal static bool IsValidImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream, false, true);
            return image.Width > 0 && image.Height > 0;
        }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { return false; }
    }

    public void Retain(IEnumerable<Guid> ids)
    {
        var retained = ids.ToHashSet();
        foreach (var id in images.Keys.Where(id => !retained.Contains(id)).ToArray())
            if (images.Remove(id, out var image)) image.Dispose();
        invalid.IntersectWith(retained);
        // Rebuild the eviction queue to avoid retaining IDs of deleted entries.
        var remaining = order.Where(images.ContainsKey).ToArray();
        order.Clear();
        foreach (var id in remaining) order.Enqueue(id);
    }

    public void Dispose()
    {
        foreach (var image in images.Values) image.Dispose();
        images.Clear(); order.Clear(); invalid.Clear();
    }
}

internal sealed class PasteReadiness
{
    internal enum Decision { Wait, Paste, Stop }
    private double? readySince;
    private bool finished;
    public Decision Update(double seconds, bool alive, bool active, bool released)
    {
        if (finished) return Decision.Stop;
        if (!alive || seconds >= 1.5) { finished = true; return Decision.Stop; }
        if (!active || !released) { readySince = null; return Decision.Wait; }
        readySince ??= seconds;
        if (seconds - readySince.Value < 0.06) return Decision.Wait;
        finished = true;
        return Decision.Paste;
    }
}

internal static class HistoryRetention
{
    internal static List<Entry> Apply(IEnumerable<Entry> entries, DateTime cutoff, int maxEntries,
        long maxBytes, Func<string, bool> sensitive)
    {
        var retained = entries.Where(entry => (entry.Pinned || entry.Created.ToUniversalTime() >= cutoff) &&
            (entry.Text is null || !sensitive(entry.Text)))
            .OrderByDescending(entry => entry.Pinned).ThenByDescending(entry => entry.Created).ToList();
        long Size(Entry entry) => entry.Image?.LongLength ?? System.Text.Encoding.UTF8.GetByteCount(entry.Text ?? "");
        var bytes = retained.Sum(Size);
        while (retained.Count > maxEntries || bytes > maxBytes)
        {
            if (retained.Count == 0) break;
            bytes -= Size(retained[^1]);
            retained.RemoveAt(retained.Count - 1);
        }
        return retained;
    }
}

internal static class HistoryPrivacy
{
    internal static bool Excludes(object? flag) => flag switch {
        int value => value == 0,
        uint value => value == 0,
        MemoryStream stream => Excludes(stream.ToArray()),
        byte[] bytes => bytes.Length < 4 || BitConverter.ToUInt32(bytes, 0) == 0,
        _ => true
    };
}
