using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SDM.Core.Util;

public enum HashAlgorithmKind { Md5, Sha1, Sha256, Sha512 }

/// <summary>Computes file checksums and compares them with the one published by the site.</summary>
public static partial class HashVerifier
{
    /// <summary>The algorithm implied by a hex checksum's length (MD5 = 32, SHA-1 = 40, SHA-256 = 64, SHA-512 = 128).</summary>
    public static HashAlgorithmKind? Detect(string? hex) => Normalize(hex)?.Length switch
    {
        32 => HashAlgorithmKind.Md5,
        40 => HashAlgorithmKind.Sha1,
        64 => HashAlgorithmKind.Sha256,
        128 => HashAlgorithmKind.Sha512,
        _ => null
    };

    /// <summary>
    /// Finds the checksum in pasted text ("SHA256: ABCD…", a line of a .sha256 file "abcd…  file.iso", or the hash alone).
    /// Returns it lower-case, or null.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var matches = HexRegex().Matches(text);
        var best = matches.Select(m => m.Value).Where(v => v.Length is 32 or 40 or 64 or 128).OrderByDescending(v => v.Length).FirstOrDefault();
        return best?.ToLowerInvariant();
    }

    public static async Task<string> ComputeAsync(string path, HashAlgorithmKind kind, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        using HashAlgorithm algorithm = kind switch
        {
            HashAlgorithmKind.Md5 => MD5.Create(),
            HashAlgorithmKind.Sha1 => SHA1.Create(),
            HashAlgorithmKind.Sha512 => SHA512.Create(),
            _ => SHA256.Create()
        };
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        long length = fs.Length, done = 0;
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            algorithm.TransformBlock(buffer, 0, read, null, 0);
            done += read;
            if (length > 0) progress?.Report(done / (double)length);
        }
        algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(algorithm.Hash!).ToLowerInvariant();
    }

    public static bool Matches(string computed, string? expected) =>
        Normalize(expected) is { } e && string.Equals(e, computed, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[0-9a-fA-F]{32,128}")]
    private static partial Regex HexRegex();
}
