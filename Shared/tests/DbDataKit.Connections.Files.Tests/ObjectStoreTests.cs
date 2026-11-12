using System.Net;
using DbDataKit.Connections.Files;

namespace DbDataKit.Connections.Files.Tests;

/// <summary>
/// Plan 27a IX06 (decision SD13: S3-compatible and Azure Blob): the connection file's rules; the signers against the examples Amazon documents (with their known signatures); list, read, write, delete against a local S3 server (Adobe's S3Mock) and Azure's own emulator (Azurite, which verifies every Shared Key signature) in containers;
/// a missing object is a permanent failure; a wrong key is a rejected login. The container tests skip, saying what to start, when the variables are not set (<c>testenv/testenv.sh up s3 azurite</c>).
/// </summary>
public sealed class ObjectStoreTests
{
    // ---- the file ----

    private static string[] Problems(string yaml) => [.. ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml, "connections/o.yml").Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.Message)];

    private const string S3 = "schema: connection/2\nkind: s3\nname: o\nendpoint: https://s3.eu-west-1.amazonaws.com\nobject_store: { bucket: reports, prefix: incoming/, region: eu-west-1 }\nsign_in: { method: access_key, user: AKIAEXAMPLE, password_secret: secret:dbdatakit:server:connection:o:password }\n";
    private const string Azure = "schema: connection/2\nkind: azure-blob\nname: o\nendpoint: https://acct.blob.core.windows.net\nobject_store: { bucket: reports }\nsign_in: { method: shared_key, user: acct, key_secret: secret:dbdatakit:server:connection:o:key }\n";

    [Fact]
    public void An_s3_and_an_azure_blob_connection_read_and_belong_to_the_file_stores()
    {
        var s3 = Support.Load(S3);
        Assert.Equal((ConnectionKinds.S3, "reports", "incoming/", "eu-west-1"), (s3.KindName, s3.ObjectStore!.Bucket, s3.ObjectStore.Prefix, s3.ObjectStore.Region));
        Assert.Equal(ConnectionKindGroup.FileStore, ConnectionKindGroups.Of(s3));
        Assert.Equal(ConnectionKindGroup.FileStore, ConnectionKindGroups.Of(Support.Load(Azure)));
        Assert.True(ConnectionKinds.IsFileStore("azure-blob") && !ConnectionKinds.IsFileServer("s3"));
        Assert.Contains(ConnectionDefinitionLoader.LoadConnectionOfAnyKind(S3.Replace("https://s3.eu-west-1.amazonaws.com", "http://minio.internal:9000"), "o.yml").Diagnostics, d => d.Message.Contains("plain http", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("kind: s3", "endpoint: https://s3.example.com\nsign_in: { method: access_key, user: a, password_secret: env:P }\n", "object_store")]
    [InlineData("kind: s3", "object_store: { bucket: b }\nsign_in: { method: access_key, user: a, password_secret: env:P }\n", "needs an `endpoint`")]
    [InlineData("kind: s3", "endpoint: https://s3.example.com\nobject_store: { bucket: b }\nsign_in: { method: shared_key, user: a, key_secret: env:K }\n", "signs in to the kind `azure-blob`")]
    [InlineData("kind: s3", "endpoint: https://s3.example.com\nobject_store: { bucket: b }\nsign_in: { method: basic, user: a, password_secret: env:P }\n", "`access_key`")]
    [InlineData("kind: azure-blob", "endpoint: https://a.blob.core.windows.net\nobject_store: { bucket: b }\nsign_in: { method: access_key, user: a, password_secret: env:P }\n", "signs in to the kind `s3`")]
    [InlineData("kind: azure-blob", "endpoint: https://a.blob.core.windows.net\nobject_store: { bucket: b, path_style: true }\nsign_in: { method: sas_token, key_secret: env:K }\n", "belong to the kind `s3`")]
    [InlineData("kind: azure-blob", "endpoint: https://a.blob.core.windows.net\nobject_store: { bucket: b }\nsign_in: { method: shared_key, user: a, key_secret: \"Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq\" }\n", "secret reference")]
    [InlineData("kind: s3", "endpoint: https://s3.example.com\nobject_store: { bucket: b, prefix: /abs }\nsign_in: { method: none }\n", "`prefix`")]
    [InlineData("kind: s3", "endpoint: \"https://s3.example.com/x?X-Amz-Signature=abc\"\nobject_store: { bucket: b }\nsign_in: { method: none }\n", "query")]
    [InlineData("kind: http", "endpoint: https://api.example.com\nobject_store: { bucket: b }\n", "`object_store` belongs")]
    public void A_connection_of_an_object_store_that_is_not_right_is_refused_with_the_reason(string kind, string rest, string expected) =>
        Assert.Contains(Problems($"schema: connection/2\n{kind}\nname: o\n{rest}"), p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));

    // ---- the signers, against what Amazon documents ----

    private static readonly DateTimeOffset AmazonExampleTime = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);
    private const string ExampleKey = "AKIAIOSFODNN7EXAMPLE";
    private const string ExampleSecret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    private static string Sign(string path, KeyValuePair<string, string>[] query, params (string Name, string Value)[] headers)
    {
        var signed = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = "examplebucket.s3.amazonaws.com", ["x-amz-content-sha256"] = AwsSigV4.EmptyPayload, ["x-amz-date"] = "20130524T000000Z" };
        foreach (var (name, value) in headers) signed[name] = value;
        return AwsSigV4.Authorization("GET", path, query, signed, AwsSigV4.EmptyPayload, ExampleKey, ExampleSecret, "us-east-1", "s3", AmazonExampleTime).Signature;
    }

    [Fact]
    public void Signature_version_4_gives_the_signatures_of_the_examples_in_amazons_documentation()
    {
        // GET Object with a Range header
        Assert.Equal("f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", Sign("/test.txt", [], ("range", "bytes=0-9")));
        // GET Bucket Lifecycle: a query parameter with no value
        Assert.Equal("fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543", Sign("/", [new("lifecycle", "")]));
        // GET Bucket (List Objects): two query parameters, sorted
        Assert.Equal("34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", Sign("/", [new("prefix", "J"), new("max-keys", "2")]));
    }

    [Fact]
    public void The_authorization_header_names_the_key_the_scope_and_the_signed_headers()
    {
        var (header, _) = AwsSigV4.Authorization("GET", "/test.txt", [], new Dictionary<string, string> { ["host"] = "examplebucket.s3.amazonaws.com", ["range"] = "bytes=0-9", ["x-amz-content-sha256"] = AwsSigV4.EmptyPayload, ["x-amz-date"] = "20130524T000000Z" }, AwsSigV4.EmptyPayload, ExampleKey, ExampleSecret, "us-east-1", "s3", AmazonExampleTime);
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", header);
        Assert.Equal("a%20b%2Bc/d~e", AwsSigV4.Encode("a b+c/d~e", keepSlash: true));
        Assert.Equal("a%2Fb", AwsSigV4.Encode("a/b", keepSlash: false));
    }

    /// <summary>Answers each request with what a test says and keeps what it received: how the stores are seen from the server's side without a server.</summary>
    private sealed class Recorder(HttpStatusCode status = HttpStatusCode.OK, string body = "") : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private const string EmptyListing = "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>false</IsTruncated></ListBucketResult>";

    [Fact]
    public async Task S3_puts_the_bucket_in_the_host_or_in_the_path_as_the_connection_says_and_signs_every_request()
    {
        var secrets = Support.Secrets(new Dictionary<string, string> { ["P"] = "secret-key-canary-77" });
        var virtualHost = Support.Load(S3.Replace("secret:dbdatakit:server:connection:o:password", "env:P"));
        var recorder = new Recorder(body: EmptyListing);
        await using (var store = await S3FileStore.OpenAsync(virtualHost, secrets, recorder, default)) await store.ListAsync("/");
        var request = Assert.Single(recorder.Requests);
        Assert.Equal("reports.s3.eu-west-1.amazonaws.com", request.RequestUri!.Host);
        Assert.Equal("/", request.RequestUri.AbsolutePath);
        Assert.Contains("prefix=incoming%2F", request.RequestUri.Query, StringComparison.Ordinal);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIAEXAMPLE/", request.Headers.GetValues("Authorization").Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key-canary-77", request.Headers.GetValues("Authorization").Single() + request.RequestUri, StringComparison.Ordinal);

        var pathStyle = Support.Load(S3.Replace("secret:dbdatakit:server:connection:o:password", "env:P").Replace("region: eu-west-1 }", "region: eu-west-1, path_style: true }"));
        var second = new Recorder(body: EmptyListing);
        await using (var store = await S3FileStore.OpenAsync(pathStyle, secrets, second, default)) await store.ListAsync("/");
        Assert.Equal(("s3.eu-west-1.amazonaws.com", "/reports"), (second.Requests[0].RequestUri!.Host, second.Requests[0].RequestUri!.AbsolutePath));

        // a public bucket is not signed
        var publicBucket = Support.Load(S3.Replace("sign_in: { method: access_key, user: AKIAEXAMPLE, password_secret: secret:dbdatakit:server:connection:o:password }", "sign_in: { method: none }"));
        var third = new Recorder(body: EmptyListing);
        await using (var store = await S3FileStore.OpenAsync(publicBucket, secrets, third, default)) await store.ListAsync("/");
        Assert.False(third.Requests[0].Headers.Contains("Authorization"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "<Error><Code>AuthenticationFailed</Code></Error>", ConnectionFailureKind.Login, false)]
    [InlineData(HttpStatusCode.NotFound, "<Error><Code>NoSuchKey</Code></Error>", ConnectionFailureKind.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, "<Error><Code>SignatureDoesNotMatch</Code></Error>", ConnectionFailureKind.Login, false)]
    [InlineData(HttpStatusCode.Forbidden, "<Error><Code>InvalidAccessKeyId</Code></Error>", ConnectionFailureKind.Login, false)]
    [InlineData(HttpStatusCode.Forbidden, "<Error><Code>AccessDenied</Code></Error>", ConnectionFailureKind.Permission, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<Error><Code>SlowDown</Code></Error>", ConnectionFailureKind.Unavailable, true)]
    [InlineData((HttpStatusCode)429, "", ConnectionFailureKind.RateLimited, true)]
    public async Task The_answers_of_the_service_are_classified_so_a_retry_knows_a_missing_object_and_a_wrong_key_from_a_busy_server(HttpStatusCode status, string body, ConnectionFailureKind kind, bool temporary)
    {
        var secrets = Support.Secrets(new Dictionary<string, string> { ["P"] = "x" });
        await using var store = await S3FileStore.OpenAsync(Support.Load(S3.Replace("secret:dbdatakit:server:connection:o:password", "env:P")), secrets, new Recorder(status, body), default);
        var failure = await Assert.ThrowsAsync<ConnectionFailureException>(() => store.OpenReadAsync("/a.csv"));
        Assert.Equal((kind, temporary), (failure.Failure.Kind, failure.Failure.IsTemporary));
        Assert.DoesNotContain("incoming", failure.Message, StringComparison.Ordinal);   // no key or address in the message
    }

    [Fact]
    public async Task A_sas_token_is_added_to_the_query_of_every_request_and_nothing_else_signs()
    {
        var secrets = Support.Secrets(new Dictionary<string, string> { ["SAS"] = "?sv=2023-11-03&sig=sas-signature-canary-1&sp=rl" });
        var connection = Support.Load(Azure.Replace("{ method: shared_key, user: acct, key_secret: secret:dbdatakit:server:connection:o:key }", "{ method: sas_token, key_secret: env:SAS }"));
        var recorder = new Recorder(body: "<EnumerationResults><Blobs/></EnumerationResults>");
        await using (var store = await AzureBlobFileStore.OpenAsync(connection, secrets, recorder, default)) await store.ListAsync("/");
        var request = Assert.Single(recorder.Requests);
        Assert.Contains("sig=sas-signature-canary-1", request.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("comp=list", request.RequestUri.Query, StringComparison.Ordinal);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Equal("/reports", request.RequestUri.AbsolutePath);
    }

    // ---- against the servers ----

    private static ConnectionDefinition S3Connection(string password = "env:SECRET", string extra = "") => Support.Load(
        $"schema: connection/2\nkind: s3\nname: o\nendpoint: {Support.Var("DBDATAKIT_TEST_S3_ENDPOINT")}\nobject_store: {{ bucket: {Support.Var("DBDATAKIT_TEST_S3_BUCKET")}, prefix: ix06/, path_style: true }}\nsign_in: {{ method: access_key, user: {Support.Var("DBDATAKIT_TEST_S3_ACCESS_KEY")}, password_secret: {password} }}\n{extra}");

    private static ConnectionDefinition AzureConnection(string key = "env:KEY", string container = "", string extra = "") => Support.Load(
        $"schema: connection/2\nkind: azure-blob\nname: o\nendpoint: {Support.Var("DBDATAKIT_TEST_AZURITE_ENDPOINT")}\nobject_store: {{ bucket: {(container.Length > 0 ? container : Support.Var("DBDATAKIT_TEST_AZURITE_CONTAINER"))}, prefix: ix06/ }}\nsign_in: {{ method: shared_key, user: {Support.Var("DBDATAKIT_TEST_AZURITE_ACCOUNT")}, key_secret: {key} }}\n{extra}");

    private static Dictionary<string, string> S3Secrets() => new() { ["SECRET"] = Support.Var("DBDATAKIT_TEST_S3_SECRET_KEY") };

    private static Dictionary<string, string> AzureSecrets() => new() { ["KEY"] = Support.Var("DBDATAKIT_TEST_AZURITE_KEY"), ["WRONG"] = Convert.ToBase64String("not-the-account-key-canary-9"u8.ToArray()) };

    [Fact]
    public async Task S3_against_a_server_lists_reads_writes_replaces_deletes_and_a_missing_object_is_permanent()
    {
        var secrets = Support.Secrets(S3Secrets());
        var folder = "/" + Guid.NewGuid().ToString("N")[..8];
        await using var store = await FileStores.OpenAsync(S3Connection(), secrets);
        await using var observer = await FileStores.OpenAsync(S3Connection(), secrets);
        await Support.ContractAsync(store, observer, folder, partFile: false);

        // folders are prefixes: a folder is listed as one, with its files under it
        await store.WriteAsync($"{folder}/deep/er/a.csv", Support.Bytes("a"));
        Assert.Contains(await store.ListAsync(folder), e => e.IsDirectory && e.Name == "deep");
        Assert.Contains(await store.ListAsync($"{folder}/deep/er"), e => e.Path == $"{folder}/deep/er/a.csv" && e.Size == 1);
        var test = await FileStoreTester.TestAsync(S3Connection(), secrets);
        Assert.True(test.Ok, test.Message);
        await store.DeleteAsync($"{folder}/deep/er/a.csv");
    }

    [Fact]
    public async Task S3_in_a_bucket_that_is_not_there_is_a_permanent_not_found()
    {
        var connection = Support.Load($"schema: connection/2\nkind: s3\nname: o\nendpoint: {Support.Var("DBDATAKIT_TEST_S3_ENDPOINT")}\nobject_store: {{ bucket: no-such-bucket-ix06, path_style: true }}\nsign_in: {{ method: access_key, user: k, password_secret: env:SECRET }}\n");
        await using var store = await FileStores.OpenAsync(connection, Support.Secrets(S3Secrets()));
        var failure = await Assert.ThrowsAsync<ConnectionFailureException>(() => store.ListAsync("/"));
        Assert.Equal(ConnectionFailureKind.NotFound, failure.Failure.Kind);
        var test = await FileStoreTester.TestAsync(connection, Support.Secrets(S3Secrets()));
        Assert.False(test.Ok);
        Assert.Equal(ConnectionFailureKind.NotFound, test.Kind);
    }

    [Fact]
    public async Task Azure_against_the_emulator_signs_every_request_lists_reads_writes_deletes_and_a_missing_blob_is_permanent()
    {
        var secrets = Support.Secrets(AzureSecrets());
        await using (var maker = await AzureBlobFileStore.OpenAsync(AzureConnection(), secrets)) await maker.EnsureContainerAsync();
        var folder = "/" + Guid.NewGuid().ToString("N")[..8];
        await using var store = await FileStores.OpenAsync(AzureConnection(), secrets);
        await using var observer = await FileStores.OpenAsync(AzureConnection(), secrets);
        await Support.ContractAsync(store, observer, folder, partFile: false);

        await store.WriteAsync($"{folder}/deep/er/a.csv", Support.Bytes("a"));
        Assert.Contains(await store.ListAsync(folder), e => e.IsDirectory && e.Name == "deep");
        Assert.Contains(await store.ListAsync($"{folder}/deep/er"), e => e.Path == $"{folder}/deep/er/a.csv" && e.Size == 1);
        var test = await FileStoreTester.TestAsync(AzureConnection(), secrets);
        Assert.True(test.Ok, test.Message);
        await store.DeleteAsync($"{folder}/deep/er/a.csv");
    }

    [Fact]
    public async Task Azure_with_a_wrong_account_key_is_a_rejected_login_and_a_container_that_is_not_there_is_not_found()
    {
        var secrets = Support.Secrets(AzureSecrets());
        await using (var maker = await AzureBlobFileStore.OpenAsync(AzureConnection(), secrets)) await maker.EnsureContainerAsync();

        await using var wrong = await FileStores.OpenAsync(AzureConnection("env:WRONG"), secrets);
        var login = await Assert.ThrowsAsync<ConnectionFailureException>(() => wrong.ListAsync("/"));
        // real Azure answers a wrong signature with AuthenticationFailed (a rejected login); the emulator says AuthorizationFailure, which real Azure uses for a missing permission: either way it is permanent and nothing is retried
        Assert.Contains(login.Failure.Kind, new[] { ConnectionFailureKind.Login, ConnectionFailureKind.Permission });
        Assert.False(login.Failure.IsTemporary);
        var rejected = await FileStoreTester.TestAsync(AzureConnection("env:WRONG"), secrets);
        Assert.False(rejected.Ok);
        Assert.False(rejected.Temporary);
        Assert.DoesNotContain("not-the-account-key", rejected.Message, StringComparison.Ordinal);

        await using var missing = await FileStores.OpenAsync(AzureConnection(container: "no-such-container-ix06"), secrets);
        var notFound = await Assert.ThrowsAsync<ConnectionFailureException>(() => missing.ListAsync("/"));
        Assert.Equal((ConnectionFailureKind.NotFound, false), (notFound.Failure.Kind, notFound.Failure.IsTemporary));
    }
}
