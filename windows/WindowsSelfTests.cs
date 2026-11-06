using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;

namespace ClipboardHistory.Windows;

internal static class WindowsSelfTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void MustFail(Action operation)
    {
        try { operation(); } catch { return; }
        throw new InvalidOperationException("Expected operation to fail.");
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "clipboard-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var secret = Encoding.UTF8.GetBytes("Disposable private clipboard note");
            var encrypted = UserDataProtection.Protect(secret);
            Require(UserDataProtection.Unprotect(encrypted).SequenceEqual(secret), "DPAPI round trip");
            Require(encrypted.AsSpan().IndexOf(secret) < 0, "No plaintext in encrypted archive");
            Require(!encrypted.SequenceEqual(UserDataProtection.Protect(secret)), "Randomized encryption");
            var tampered = encrypted.ToArray(); tampered[^1] ^= 1;
            MustFail(() => UserDataProtection.Unprotect(tampered));
            MustFail(() => UserDataProtection.Unprotect(secret));

            using var image = new Bitmap(320, 160);
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.Coral);
            using var thumbnail = HistoryThumbnailCache.Resize(image, 112);
            Require(thumbnail.Width == 112 && thumbnail.Height == 56, "Aspect-preserving bounded thumbnail");
            using var png = new MemoryStream(); image.Save(png, ImageFormat.Png);
            var imageBytes = png.ToArray();
            Require(HistoryThumbnailCache.IsValidImage(imageBytes), "PNG validation");
            Require(!HistoryThumbnailCache.IsValidImage(secret), "Invalid image rejection");
            var note = new Entry { Text = Encoding.UTF8.GetString(secret), Fingerprint = "note", Pinned = true, Favorite = true };
            var picture = new Entry { Image = imageBytes, Fingerprint = "image" };
            var broken = new Entry { Image = secret, Fingerprint = "broken" };
            var legacy = Path.Combine(root, "history.json");
            File.WriteAllText(legacy, JsonSerializer.Serialize(new[] { note, picture, broken }));
            using (var store = new WindowsHistoryStorage(root))
            {
                var result = store.Load();
                Require(result.Entries.Count == 2 && result.Skipped == 1, "Recover around corrupt image");
                Require(result.Entries[0].Pinned && result.Entries[0].Favorite, "Metadata retained");
                Require(!File.Exists(legacy), "Plaintext removed after migration");
                MustFail(() => { using var other = new WindowsHistoryStorage(root); });
            }
            var path = Path.Combine(root, "history-v1.enc");
            Require(File.ReadAllBytes(path).AsSpan().IndexOf(secret) < 0, "Encrypted migrated history");
            using (var store = new WindowsHistoryStorage(root))
            {
                var result = store.Load();
                Require(result.Entries.Count == 2, "Encrypted restart");
                using var cache = new HistoryThumbnailCache();
                Require(cache.Get(result.Entries[1]) is { Width: 112, Height: 56 }, "Thumbnail cache");
                cache.Retain([note.Id]);
                store.Save([note]);
            }
            using (var store = new WindowsHistoryStorage(root))
                Require(store.Load().Entries.Count == 1, "Deletion persisted");

            var corrupted = File.ReadAllBytes(path); corrupted[^1] ^= 1; File.WriteAllBytes(path, corrupted);
            File.WriteAllText(legacy, JsonSerializer.Serialize(new[] { note }));
            using (var store = new WindowsHistoryStorage(root)) MustFail(() => store.Load());
            Require(File.ReadAllBytes(path).SequenceEqual(corrupted) && File.Exists(legacy), "No plaintext fallback or reset after authentication failure");

            var failed = Path.Combine(root, "failed"); Directory.CreateDirectory(failed);
            File.WriteAllText(Path.Combine(failed, "history.json"), JsonSerializer.Serialize(new[] { note }));
            Directory.CreateDirectory(Path.Combine(failed, "history-v1.enc.tmp"));
            using (var store = new WindowsHistoryStorage(failed)) MustFail(() => store.Load());
            Require(File.Exists(Path.Combine(failed, "history.json")), "Failed migration keeps originals");

            var cutoff = DateTime.UtcNow;
            var expired = new Entry { Text = "old", Created = cutoff.AddHours(-1), Favorite = true };
            var pinned = new Entry { Text = "pinned", Created = cutoff.AddHours(-1), Pinned = true };
            var recent = new Entry { Text = "recent", Created = cutoff.AddHours(1) };
            var kept = HistoryRetention.Apply([recent, pinned, expired], cutoff, 2, 100, _ => false);
            Require(kept.Select(entry => entry.Id).SequenceEqual(new[] { pinned.Id, recent.Id }), "Pin order and retention");
            Require(HistoryRetention.Apply([recent, pinned], cutoff, 1, 100, _ => false)[0].Id == pinned.Id, "Pins preferred under quota");
            Require(HistoryRetention.Apply([pinned], cutoff, 10, 1, _ => false).Count == 0, "Pinned entries respect bytes quota");
            Require(HistoryRetention.Apply([pinned], cutoff, 10, 100, _ => true).Count == 0, "Pins respect privacy filters");

            var readiness = new PasteReadiness();
            Require(readiness.Update(0, true, false, false) == PasteReadiness.Decision.Wait, "Wait for focus");
            Require(readiness.Update(0.4, true, true, false) == PasteReadiness.Decision.Wait, "Wait for keys");
            Require(readiness.Update(0.6, true, true, true) == PasteReadiness.Decision.Wait, "Require stable readiness");
            Require(readiness.Update(0.64, true, false, true) == PasteReadiness.Decision.Wait, "Reset after focus loss");
            Require(readiness.Update(0.7, true, true, true) == PasteReadiness.Decision.Wait, "Wait again");
            Require(readiness.Update(0.8, true, true, true) == PasteReadiness.Decision.Paste, "Paste once");
            Require(readiness.Update(0.9, true, true, true) == PasteReadiness.Decision.Stop, "No repeated paste");
            Require(new PasteReadiness().Update(1.5, true, true, true) == PasteReadiness.Decision.Stop, "Timeout");
            Require(new PasteReadiness().Update(0, false, false, true) == PasteReadiness.Decision.Stop, "Gone destination");
            Require(ClipboardForm.InputStructureSize == (IntPtr.Size == 8 ? 40 : 28), "Native INPUT structure layout");
            Require(HistoryPrivacy.Excludes(0) && HistoryPrivacy.Excludes(new byte[4]) && HistoryPrivacy.Excludes(null), "Private and unknown flags excluded");
            Require(!HistoryPrivacy.Excludes(1u) && !HistoryPrivacy.Excludes(new MemoryStream(BitConverter.GetBytes(1u))), "Allowed flags recognized");
            Console.WriteLine("Windows encryption, migration, recovery, thumbnails, retention, and paste tests OK");
        }
        finally { Directory.Delete(root, true); }
    }
}
