using DbDataKit.Knowledge.Onnx;
using DbDataKit.State;
using DbDataKit.State.Tests;
using Microsoft.Extensions.AI;

namespace DbDataKit.Knowledge.Onnx.Tests;

/// <summary>
/// Plan 27a KN06: the built-in embedding model. What needs no model file runs everywhere (the catalog and its licences, the hash check, the kind's validator); what runs the model needs its files, from <c>DBDATAKIT_TEST_ONNX_MODEL</c> (a folder that holds them) or fetched once into the
/// temporary folder when <c>DBDATAKIT_TEST_ONNX_FETCH=true</c>, and is skipped without them (the 90 MB file is not kept in the repository).
/// </summary>
public sealed class OnnxEmbeddingTests : IDisposable
{
    private static readonly BuiltInModel MiniLm = BuiltInEmbeddingModels.Find(BuiltInEmbeddingModels.MiniLm)!;
    private static readonly Lazy<string?> Folder = new(FindModel);
    private readonly StateEngines _engines = new();
    private readonly List<string> _temporary = [];

    public void Dispose() { _engines.Dispose(); foreach (var t in _temporary) try { Directory.Delete(t, true); } catch (IOException) { } }

    private static string? FindModel()
    {
        var given = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_ONNX_MODEL");
        if (!string.IsNullOrEmpty(given) && BuiltInEmbeddingModels.Verify(given, MiniLm).Count == 0) return given;
        if (!string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_ONNX_FETCH"), "true", StringComparison.OrdinalIgnoreCase)) return null;
        var cache = Path.Combine(Path.GetTempPath(), "dbdatakit-onnx-" + MiniLm.Model.Sha256[..12]);
        BuiltInEmbeddingModels.FetchAsync(cache, MiniLm, new HttpClient { Timeout = TimeSpan.FromMinutes(10) }).GetAwaiter().GetResult();
        return cache;
    }

    private static string ModelOrSkip()
    {
        Assert.SkipWhen(Folder.Value is null, "set DBDATAKIT_TEST_ONNX_MODEL to a folder with the model's files, or DBDATAKIT_TEST_ONNX_FETCH=true, to run the model");
        return Folder.Value!;
    }

    private string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "onnx-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        _temporary.Add(path);
        return path;
    }

    // ---- no model file needed ----

    [Fact]
    public void The_catalog_offers_only_models_whose_licence_is_recorded_with_where_it_is_stated_and_where_the_files_come_from()
    {
        Assert.NotEmpty(BuiltInEmbeddingModels.Offered);
        Assert.All(BuiltInEmbeddingModels.Offered, m =>
        {
            Assert.True(m.LicenceRecorded);
            Assert.StartsWith("builtin/", m.Id);
            Assert.All(m.Files, f => { Assert.Equal(64, f.Sha256.Length); Assert.StartsWith("https://", f.Url); Assert.True(f.Size > 0); });
            Assert.Contains("revision", m.Source);    // pinned: a moving name is not a version
        });
        Assert.Equal(("Apache-2.0", 384), (MiniLm.Licence, MiniLm.Dimension));

        // a row with no licence is not offered, whatever else it says
        var unlicensed = MiniLm with { Id = "builtin/unlicensed", Licence = "" };
        Assert.False(unlicensed.LicenceRecorded);
        Assert.False(MiniLm with { LicenceUrl = "" } is { LicenceRecorded: true });
        Assert.Null(BuiltInEmbeddingModels.Find("builtin/unlicensed"));
        Assert.Null(BuiltInEmbeddingModels.Find("builtin/nothing"));
    }

    [Fact]
    public void A_file_that_is_missing_or_is_not_the_recorded_one_is_refused_before_anything_is_loaded()
    {
        var folder = Scratch();
        var missing = BuiltInEmbeddingModels.Verify(folder, MiniLm);
        Assert.Equal(2, missing.Count);
        Assert.Contains("model.onnx is missing", missing[0]);

        File.WriteAllText(Path.Combine(folder, "model.onnx"), "not the model");
        File.WriteAllText(Path.Combine(folder, "vocab.txt"), "not the vocabulary");
        var wrong = BuiltInEmbeddingModels.Verify(folder, MiniLm);
        Assert.All(wrong, p => Assert.Contains("is not the file of builtin/all-minilm-l6-v2", p));
        var refused = Assert.Throws<InvalidDataException>(() => new OnnxEmbeddingGenerator(folder, MiniLm));
        Assert.Contains("SHA-256", refused.Message);
    }

    [Fact]
    public async Task A_fetch_that_gives_another_file_keeps_nothing()
    {
        var folder = Scratch();
        var http = new HttpClient(new Stub("another file entirely"));
        await Assert.ThrowsAsync<InvalidDataException>(() => BuiltInEmbeddingModels.FetchAsync(folder, MiniLm, http));
        Assert.Empty(Directory.EnumerateFiles(folder));   // neither the file nor a half file is left
    }

    private sealed class Stub(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage { Content = new StringContent(body) });
    }

    [Fact]
    public void The_kind_takes_exactly_the_catalogs_files_with_the_catalogs_hashes()
    {
        var kind = ModelFiles.Registry().Require("embedding-model");
        Assert.Equal(["serving", "runner"], kind.TargetTypes);
        Assert.Equal(PublishMode.Serve, kind.DefaultMode);
        Assert.Equal(1, kind.FormatOf!(ModelFiles.Manifest(MiniLm)));

        string? Validate(string name, string manifest, params ArtifactRef[] files) => kind.Validator!(new DefinitionCandidate("embedding-model", name, manifest, files, [], 1));
        ArtifactRef Of(BuiltInFile f, string? sha = null) => new(f.Name, sha ?? f.Sha256, f.Size, null);

        Assert.Null(Validate("all-minilm-l6-v2", ModelFiles.Manifest(MiniLm), Of(MiniLm.Model), Of(MiniLm.Vocabulary)));
        Assert.Contains("is not the file of builtin/all-minilm-l6-v2", Validate("all-minilm-l6-v2", ModelFiles.Manifest(MiniLm), Of(MiniLm.Model, new string('0', 64)), Of(MiniLm.Vocabulary))!);   // a substituted model never reaches a channel
        Assert.Contains("vocab.txt is not among", Validate("all-minilm-l6-v2", ModelFiles.Manifest(MiniLm), Of(MiniLm.Model))!);
        Assert.Contains("extra.bin is not a file of", Validate("all-minilm-l6-v2", ModelFiles.Manifest(MiniLm), Of(MiniLm.Model), Of(MiniLm.Vocabulary), new ArtifactRef("extra.bin", new string('a', 64), 1, null))!);
        Assert.Contains("not one this product offers", Validate("x", "embedding-model: 1\nid: builtin/other\n", Of(MiniLm.Model))!);
        Assert.Contains("published as", Validate("another-name", ModelFiles.Manifest(MiniLm), Of(MiniLm.Model), Of(MiniLm.Vocabulary))!);
    }

    // ---- with the model's files ----

    [Fact]
    public void A_fixed_sentence_gives_the_recorded_vector_within_a_tolerance_at_unit_length_and_the_tokens_are_the_models()
    {
        using var generator = new OnnxEmbeddingGenerator(ModelOrSkip(), MiniLm);

        Assert.Equal([101, 7592, 2088, 102], generator.Tokens("Hello world"));           // [CLS] hello world [SEP], lower cased
        var vector = generator.GenerateAsync(["The quick brown fox jumps over the lazy dog."]).GetAwaiter().GetResult().Single().Vector.ToArray();
        Assert.Equal(384, vector.Length);
        Assert.Equal(1.0, Math.Sqrt(vector.Sum(x => (double)x * x)), 1e-4);
        // the first twelve numbers of the vector as this model gives them on Linux (recorded 2026-11-12); another machine's CPU may differ in the last digits, never in the first three
        float[] recorded = [0.04393354f, 0.058934394f, 0.048178393f, 0.07754807f, 0.026744395f, -0.03762962f, -0.0026050876f, -0.05994305f, -0.0024960525f, 0.022072768f, 0.048025865f, 0.05575533f];
        for (var i = 0; i < recorded.Length; i++) Assert.Equal(recorded[i], vector[i], 1e-3f);
    }

    [Fact]
    public void Meaning_is_near_and_other_things_are_far_and_a_text_alone_is_the_same_vector_in_a_batch_with_a_longer_one()
    {
        using var generator = new OnnxEmbeddingGenerator(ModelOrSkip(), MiniLm);
        var vectors = generator.GenerateAsync(["Revenue is the money the company took in.", "Turnover means the income of a firm.", "The cat sat on the warm windowsill.", "Turnover means the income of a firm. " + string.Join(' ', Enumerable.Repeat("It is counted when the order ships.", 12))])
            .GetAwaiter().GetResult().Select(e => e.Vector.ToArray()).ToList();
        double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();

        Assert.True(Cosine(vectors[0], vectors[1]) > Cosine(vectors[0], vectors[2]) + 0.2, "turnover is nearer to revenue than a cat is");
        var alone = generator.GenerateAsync(["Turnover means the income of a firm."]).GetAwaiter().GetResult().Single().Vector.ToArray();
        var inBatch = generator.GenerateAsync(["Turnover means the income of a firm.", vectors.Count.ToString() + " " + string.Join(' ', Enumerable.Repeat("a long text that pads the batch", 20))]).GetAwaiter().GetResult().First().Vector.ToArray();
        Assert.True(Cosine(alone, inBatch) > 0.9999, "padding does not change a vector");
    }

    [Fact]
    public void A_text_longer_than_the_model_reads_is_cut_and_the_closing_token_is_kept()
    {
        using var generator = new OnnxEmbeddingGenerator(ModelOrSkip(), MiniLm);
        var tokens = generator.Tokens(string.Join(' ', Enumerable.Repeat("word", 1000)));
        Assert.Equal(MiniLm.MaxTokens, tokens.Count);
        Assert.Equal(102, tokens[^1]);
        Assert.Equal(101, tokens[0]);
    }

    [Fact]
    public void The_files_are_published_as_an_artifact_and_restored_from_the_payload_store_by_hash()
    {
        var folder = ModelOrSkip();
        var database = _engines.Provision("Sqlite").Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "main"), time: new StateTestClock(DateTimeOffset.Parse("2026-11-12T12:00:00Z")));
        var payloads = new PayloadStore(database);
        var publisher = new DefinitionPublisher(database, ModelFiles.Registry(), payloads, "serving");

        var one = ModelFiles.Publish(publisher, new PublishArtifacts(payloads, publisher, "ada"), folder, MiniLm, "ada");
        Assert.True(one.Changed);
        Assert.False(ModelFiles.Publish(publisher, new PublishArtifacts(payloads, publisher, "ada"), folder, MiniLm, "ada").Changed);   // the same files again change nothing

        var restored = Scratch();
        ModelFiles.Restore(publisher, payloads, MiniLm, 1, restored);
        Assert.Empty(BuiltInEmbeddingModels.Verify(restored, MiniLm));
        using var generator = new OnnxEmbeddingGenerator(restored, MiniLm);
        Assert.Equal(384, generator.GenerateAsync(["from the state"]).GetAwaiter().GetResult().Single().Vector.Length);
    }
}
