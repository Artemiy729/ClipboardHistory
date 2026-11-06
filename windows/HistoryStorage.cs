using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipboardHistory.Windows;

internal static class UserDataProtection
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("CHWIN1");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClipboardHistory/windows/history/v1");
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] plaintext) => [.. Header, .. Transform(plaintext, true)];

    public static byte[] Unprotect(byte[] ciphertext)
    {
        if (!ciphertext.AsSpan().StartsWith(Header)) throw new CryptographicException("Unsupported encrypted history format.");
        return Transform(ciphertext[Header.Length..], false);
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new Blob();
        try
        {
            var input = new Blob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
            var entropy = new Blob { Size = Entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
            // Current-user scope: deliberately do not set CRYPTPROTECT_LOCAL_MACHINE.
            var succeeded = protect
                ? CryptProtectData(ref input, "Clipboard History", ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!succeeded) throw new CryptographicException("Windows could not protect or authenticate history.", new Win32Exception(Marshal.GetLastWin32Error()));
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (!protect)
                {
                    var zeros = new byte[Math.Min(output.Size, 8192)];
                    for (var offset = 0; offset < output.Size; offset += zeros.Length)
                        Marshal.Copy(zeros, 0, IntPtr.Add(output.Data, offset), Math.Min(zeros.Length, output.Size - offset));
                }
                LocalFree(output.Data);
            }
            pinned.Free(); entropyPin.Free();
        }
    }
}

internal sealed class WindowsHistoryStorage : IDisposable
{
    private sealed class Archive {
        public Archive() { }
        public int Version { get; set; } = 1;
        public List<Entry> Entries { get; set; } = [];
    }
    private readonly string encryptedPath;
    private readonly string legacyPath;
    private readonly FileStream writerLock;
    public WindowsHistoryStorage(string folder)
    {
        Directory.CreateDirectory(folder);
        encryptedPath = Path.Combine(folder, "history-v1.enc");
        legacyPath = Path.Combine(folder, "history.json");
        writerLock = new FileStream(Path.Combine(folder, "history.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public (List<Entry> Entries, int Skipped) Load()
    {
        List<Entry> entries;
        if (File.Exists(encryptedPath)) entries = Decode(File.ReadAllBytes(encryptedPath));
        else if (File.Exists(legacyPath))
            entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllBytes(legacyPath))
                ?? throw new JsonException("Invalid legacy history.");
        else entries = [];

        var usable = entries.Where(entry => entry.Text is not null ||
            entry.Image is byte[] data && HistoryThumbnailCache.IsValidImage(data)).ToList();
        // Commit and verify before removing any plaintext migration input.
        Save(usable);
        _ = Decode(File.ReadAllBytes(encryptedPath));
        if (File.Exists(legacyPath)) File.Delete(legacyPath);
        // Previous releases wrote plaintext to this temporary file too.
        var oldTemporary = legacyPath + ".tmp";
        if (File.Exists(oldTemporary)) File.Delete(oldTemporary);
        return (usable, entries.Count - usable.Count);
    }

    public void Save(IReadOnlyCollection<Entry> entries)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Archive { Entries = entries.ToList() });
        try
        {
            var ciphertext = UserDataProtection.Protect(plaintext);
            var temporary = encryptedPath + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(ciphertext);
                    stream.Flush(true);
                }
                File.Move(temporary, encryptedPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static List<Entry> Decode(byte[] ciphertext)
    {
        var plaintext = UserDataProtection.Unprotect(ciphertext);
        try
        {
            var archive = JsonSerializer.Deserialize<Archive>(plaintext);
            if (archive is null || archive.Version != 1 || archive.Entries is null)
                throw new JsonException("Unsupported encrypted history version.");
            return archive.Entries;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Dispose() => writerLock.Dispose();
}
