namespace DbDataKit.Views;

/// <summary>
/// Whether bytes may be held as an image widget's picture (plan 32 DBR10): the .NET port of <c>checkImageBytes</c> and <c>IMAGE_MAX_BYTES</c> of <c>@dbdatakit/visuals</c> (<c>widgets/ImageBlock.tsx</c>), with the
/// same limits and the same words. A png, jpeg or webp **by its first bytes** (so an SVG, which can script, and a renamed file are refused whatever they are called), of at most <see cref="MaxBytes"/>.
/// </summary>
public static class ImageCheck
{
    /// <summary>1 MiB, <c>IMAGE_MAX_BYTES</c> of the renderers.</summary>
    public const int MaxBytes = 1_048_576;

    /// <summary>The media types a widget holds, the same three as <c>ImageDoc['media_type']</c>.</summary>
    public static IReadOnlyList<string> MediaTypes { get; } = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>The refusal in words (code <see cref="ViewsCodes.UnsafeImage"/>), or null when the bytes may be held.</summary>
    public static string? Refusal(ReadOnlySpan<byte> bytes, string mediaType)
    {
        if (!MediaTypes.Contains(mediaType, StringComparer.Ordinal))
            return $"`{mediaType}` is not an image type a widget holds: png, jpeg or webp (never SVG, which can script).";
        if (bytes.Length > MaxBytes) return $"The image is {bytes.Length} bytes; a widget's image is at most {MaxBytes}.";
        return Matches(bytes, mediaType) ? null : $"The bytes are not a {mediaType}.";
    }

    private static bool Matches(ReadOnlySpan<byte> b, string mediaType) => mediaType switch
    {
        "image/png" => b.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
        "image/jpeg" => b.Length >= 3 && b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff,
        "image/webp" => b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };
}
