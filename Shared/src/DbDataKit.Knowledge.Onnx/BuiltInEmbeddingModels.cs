using System.Security.Cryptography;

namespace DbDataKit.Knowledge.Onnx;

/// <summary>One file of a built-in model: its name inside the model's folder, its SHA-256 (a file is used only when its hash is this one) and where it can be fetched from by a machine that may reach the internet.</summary>
public sealed record BuiltInFile(string Name, string Sha256, long Size, string Url);

/// <summary>
/// A model the product offers to embed with in its own process (plan 27a KN06, decision SD12). <b>A model whose licence is not recorded is not offered</b> (<see cref="BuiltInEmbeddingModels.Offered"/>): the licence, where it says so and where the files come from are part of the row, so adding a model is a review of
/// what it may be used for.
/// </summary>
/// <param name="Title">What it is called on a screen.</param>
/// <param name="Id">The id the index records the model under, <c>builtin/</c> and a name.</param>
/// <param name="Dimension">How many numbers a vector has.</param>
/// <param name="MaxTokens">The longest text it reads, in tokens; a longer text is cut (the chunker keeps chunks well inside it).</param>
/// <param name="Licence">The licence's name (an SPDX identifier); empty when it is not recorded.</param>
/// <param name="LicenceUrl">Where the licence is stated.</param>
/// <param name="Source">Where the model comes from, pinned to a revision.</param>
/// <param name="Model">The ONNX file.</param>
/// <param name="Vocabulary">The WordPiece vocabulary.</param>
public sealed record BuiltInModel(string Id, string Title, int Dimension, int MaxTokens, string Licence, string LicenceUrl, string Source, BuiltInFile Model, BuiltInFile Vocabulary)
{
    public IReadOnlyList<BuiltInFile> Files => [Model, Vocabulary];

    public bool LicenceRecorded => Licence.Length > 0 && LicenceUrl.Length > 0;
}

/// <summary>The models offered, and the checking of their files.</summary>
public static class BuiltInEmbeddingModels
{
    /// <summary>The id of the one model offered today.</summary>
    public const string MiniLm = "builtin/all-minilm-l6-v2";

    private const string Revision = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41";

    /// <summary>Every model known, whether its licence is recorded or not.</summary>
    public static IReadOnlyList<BuiltInModel> Known { get; } =
    [
        new(MiniLm, "all-MiniLM-L6-v2 (sentence embeddings, English, 384 dimensions)", 384, 256, "Apache-2.0", "https://www.apache.org/licenses/LICENSE-2.0",
            $"https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2 at revision {Revision}",
            new BuiltInFile("model.onnx", "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452", 90_405_214, $"https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/{Revision}/onnx/model.onnx"),
            new BuiltInFile("vocab.txt", "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3", 231_508, $"https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/{Revision}/vocab.txt")),
    ];

    /// <summary>The models that may be offered: the ones whose licence is recorded.</summary>
    public static IReadOnlyList<BuiltInModel> Offered => [.. Known.Where(m => m.LicenceRecorded)];

    public static BuiltInModel? Find(string id) => Offered.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>The SHA-256 of a file, as lower-case hex.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>What is wrong with the files of a model in a folder, in words: a file missing, or one whose hash is not the recorded one (a damaged or substituted file is never loaded). Empty when the folder holds the model.</summary>
    public static IReadOnlyList<string> Verify(string folder, BuiltInModel model)
    {
        var problems = new List<string>();
        foreach (var file in model.Files)
        {
            var path = Path.Combine(folder, file.Name);
            if (!File.Exists(path)) problems.Add($"{file.Name} is missing from {folder}.");
            else if (new FileInfo(path).Length != file.Size || !string.Equals(HashOf(path), file.Sha256, StringComparison.Ordinal)) problems.Add($"{file.Name} in {folder} is not the file of {model.Id} (its SHA-256 is not {file.Sha256}).");
        }
        return problems;
    }

    /// <summary>
    /// Fetches the files of a model into a folder from where the catalog says, each checked against its hash before it is kept (it is written beside its place and moved there, so a half file is never used). For a machine that may reach the internet; an offline install gets the files from its own
    /// state (<see cref="ModelFiles"/>).
    /// </summary>
    public static async Task FetchAsync(string folder, BuiltInModel model, HttpClient http, CancellationToken cancellation = default)
    {
        Directory.CreateDirectory(folder);
        foreach (var file in model.Files)
        {
            var target = Path.Combine(folder, file.Name);
            if (File.Exists(target) && HashOf(target) == file.Sha256) continue;
            var temporary = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            try
            {
                using (var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                    await using var destination = File.Create(temporary);
                    await source.CopyToAsync(destination, cancellation).ConfigureAwait(false);
                }
                if (HashOf(temporary) != file.Sha256) throw new InvalidDataException($"{file.Url} gave a file whose SHA-256 is not {file.Sha256}; it is not kept.");
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
