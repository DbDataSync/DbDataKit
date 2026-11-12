using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Config.Git;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Management;

namespace DbDataGateway.Tests.Config;

/// <summary>Plan 33, UE17: <c>server.yaml</c> through the management API: read with every secret masked, check a draft with the shared validator, edit a field of the draft, write as a commit of the configuration repository.</summary>
public sealed partial class ManagementTests
{
    private const string TlsSecret = Sentinel + "-tls";

    private static JsonNode ParseBody(string text) => JsonNode.Parse(text)!;

    private async Task<(HttpStatusCode Status, JsonNode Body)> SettingsAsync(PairedServer admin, HttpMethod method, string suffix, object? body = null, ServerRole roles = ServerRole.Admin)
    {
        var response = await CallAsync(admin, method, "/api/v1/config/server.yaml" + suffix, roles, body is null ? null : System.Text.Json.JsonSerializer.Serialize(body));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? ParseBody(text) : new JsonObject());
    }

    private string ServerFile => Path.Combine(ConfigDirectory, "server.yaml");

    [Fact]
    public async Task No_role_below_administrator_reads_checks_edits_or_writes_server_yaml_and_nothing_changes_for_them()
    {
        await StartAsync();
        var admin = await PairAsync(ServerRole.Admin | ServerRole.Author | ServerRole.Approver);
        var before = File.ReadAllText(ServerFile);
        var history = new GitCommitService(ConfigDirectory).GetHistory("", 50).Count;
        foreach (var held in new[] { ServerRole.Viewer, ServerRole.Operator, ServerRole.Author, ServerRole.Approver })
        {
            var roles = ServerRoles.Close(held);
            Assert.Equal(HttpStatusCode.Forbidden, (await SettingsAsync(admin, HttpMethod.Get, "", null, roles)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SettingsAsync(admin, HttpMethod.Post, "/check", new { text = before }, roles)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SettingsAsync(admin, HttpMethod.Post, "/edit", new { text = before }, roles)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await SettingsAsync(admin, HttpMethod.Put, "", new { text = before + "logSql: false\n" }, roles)).Status);
        }
        Assert.Equal(before, File.ReadAllText(ServerFile));
        Assert.Equal(history, new GitCommitService(ConfigDirectory).GetHistory("", 50).Count);
        Assert.DoesNotContain(ConfigTestSupport.AuditEntries(dataDirectory), e => e.ToJsonString().Contains("server.yaml", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await SettingsAsync(admin, HttpMethod.Delete, "")).Status);
    }

    [Fact]
    public async Task Server_yaml_is_read_with_its_secrets_masked_and_a_masked_value_keeps_the_secret_when_it_is_written_back()
    {
        await StartAsync();
        var admin = await PairAsync();
        var read = await SettingsAsync(admin, HttpMethod.Get, "");
        Assert.Equal(HttpStatusCode.OK, read.Status);
        var text = (string)read.Body["text"]!;
        Assert.DoesNotContain(TlsSecret, read.Body.ToJsonString());
        Assert.Contains("certificatePassword: '********'", text);
        Assert.True((bool)read.Body["masked"]!);
        Assert.Contains("instanceId", read.Body["protected"]!.ToJsonString());
        var version = (string)read.Body["version"]!;
        Assert.Equal(64, version.Length);

        // the masked text comes back with one more setting: the secret is kept in the file, the setting is added, the commit is the caller's
        var put = await SettingsAsync(admin, HttpMethod.Put, "", new { text = text.TrimEnd() + "\nlogSql: false\n", expected_version = version, note = "quieter" });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal("logSql", (string)put.Body["changed"]![0]!);
        Assert.False((bool)put.Body["restart_needed"]!);
        var onDisk = File.ReadAllText(ServerFile);
        Assert.Contains($"certificatePassword: {TlsSecret}", onDisk);
        Assert.Contains("logSql: false", onDisk);
        Assert.DoesNotContain("********", onDisk);
        Assert.Empty(Directory.GetFiles(ConfigDirectory, "*.tmp-*", SearchOption.AllDirectories));   // the write is a temporary file moved over, none is left
        var commit = new GitCommitService(ConfigDirectory).GetHistory("", 1)[0];
        Assert.Equal((string)put.Body["commit"]!, commit.Sha);
        Assert.Equal("Edit server.yaml (logSql): quieter", commit.Message.Trim());
        Assert.Equal("Ada", commit.AuthorName);
        Assert.Equal((string)put.Body["version"]!, (string)(await SettingsAsync(admin, HttpMethod.Get, "")).Body["version"]!);

        // a secret typed over the mask replaces it, and is never in an answer
        var read2 = await SettingsAsync(admin, HttpMethod.Get, "");
        var replaced = await SettingsAsync(admin, HttpMethod.Put, "", new { text = ((string)read2.Body["text"]!).Replace("certificatePassword: '********'", "certificatePassword: NEW-tls-pw-73"), expected_version = (string)read2.Body["version"]! });
        Assert.Equal(HttpStatusCode.OK, replaced.Status);
        Assert.Contains("certificatePassword: NEW-tls-pw-73", File.ReadAllText(ServerFile));
        Assert.DoesNotContain("NEW-tls-pw-73", replaced.Body.ToJsonString());
        Assert.DoesNotContain("NEW-tls-pw-73", (await SettingsAsync(admin, HttpMethod.Get, "")).Body.ToJsonString());

        // a mask where the file keeps no secret would store the mask as a password: refused in words
        var read3 = await SettingsAsync(admin, HttpMethod.Get, "");
        var bad = await SettingsAsync(admin, HttpMethod.Put, "", new { text = (string)read3.Body["text"]! + "\nsomePassword: '********'\n", expected_version = (string)read3.Body["version"]! });
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Contains("has none to keep", (string)bad.Body["error"]!);

        // the audit names who wrote what and the outcome; it carries no text and no secret
        var audit = ConfigTestSupport.AuditEntries(dataDirectory).Select(e => e.ToJsonString()).ToList();
        Assert.Contains(audit, e => e.Contains("server.yaml write", StringComparison.Ordinal) && e.Contains("written: logSql", StringComparison.Ordinal) && e.Contains("\"userId\":\"ada\"", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Contains("server.yaml write", StringComparison.Ordinal) && e.Contains("refused", StringComparison.Ordinal));
        Assert.All(audit, e => { Assert.DoesNotContain("NEW-tls-pw-73", e); Assert.DoesNotContain(TlsSecret, e); Assert.DoesNotContain("quieter", e); });
    }

    [Fact]
    public async Task The_semantic_level_and_combine_limits_are_edited_as_fields_and_a_limit_out_of_range_is_refused_with_its_key()
    {
        // plan 14a SL11: the scalar part of semantic:, edited through the same set path as the other fields
        await StartAsync();
        var admin = await PairAsync();
        var text = ((string)(await SettingsAsync(admin, HttpMethod.Get, "")).Body["text"]!).TrimEnd();

        var edited = await SettingsAsync(admin, HttpMethod.Post, "/edit", new { text, set = new Dictionary<string, object> { ["semantic.level"] = 3, ["semantic.combine.memoryMb"] = 512, ["semantic.combine.timeoutSeconds"] = 30, ["semantic.combine.maxInputRows"] = 1000 } });
        Assert.Equal(HttpStatusCode.OK, edited.Status);
        var draft = (string)edited.Body["text"]!;
        var good = await SettingsAsync(admin, HttpMethod.Post, "/check", new { text = draft });
        Assert.True((bool)good.Body["valid"]!, good.Body.ToJsonString());
        var fields = good.Body["fields"]!;
        Assert.Equal((3, 512, 30, 1000), ((int)fields["semantic.level"]!, (int)fields["semantic.combine.memoryMb"]!, (int)fields["semantic.combine.timeoutSeconds"]!, (int)fields["semantic.combine.maxInputRows"]!));

        var tooFew = await SettingsAsync(admin, HttpMethod.Post, "/edit", new { text, set = new Dictionary<string, object> { ["semantic.combine.memoryMb"] = 4 } });
        var bad = await SettingsAsync(admin, HttpMethod.Post, "/check", new { text = (string)tooFew.Body["text"]! });
        Assert.False((bool)bad.Body["valid"]!);
        Assert.Contains(bad.Body["diagnostics"]!.AsArray(), d => ((string?)d!["key"])?.EndsWith("MemoryMb", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task A_draft_is_checked_by_the_shared_validator_with_the_field_that_is_wrong_and_nothing_is_written_when_it_is_not_valid()
    {
        await StartAsync();
        var admin = await PairAsync();
        var read = await SettingsAsync(admin, HttpMethod.Get, "");
        var text = ((string)read.Body["text"]!).TrimEnd();
        var version = (string)read.Body["version"]!;
        var before = File.ReadAllText(ServerFile);
        var history = new GitCommitService(ConfigDirectory).GetHistory("", 50).Count;

        var good = await SettingsAsync(admin, HttpMethod.Post, "/check", new { text = text + "\nviews: { cacheSeconds: 60 }\nmodules: [flightsql, views]\n" });
        Assert.True((bool)good.Body["valid"]!, good.Body.ToJsonString());
        Assert.Equal(60, (int)good.Body["fields"]!["views.cacheSeconds"]!);
        Assert.Equal(["flightsql", "views"], good.Body["fields"]!["modules"]!.AsArray().Select(m => (string)m!).ToArray());
        Assert.Equal(["views", "modules"], good.Body["changed"]!.AsArray().Select(m => (string)m!).ToArray());
        Assert.Equal(["views", "modules"], good.Body["restart_keys"]!.AsArray().Select(m => (string)m!).ToArray());
        Assert.Empty(good.Body["problems"]!.AsArray());

        // a value out of its range: the diagnostic names the key, the file and the line of the draft, and the file is not touched
        var draft = text + "\nviews:\n  cacheSeconds: 99999\n";
        var bad = await SettingsAsync(admin, HttpMethod.Post, "/check", new { text = draft });
        Assert.False((bool)bad.Body["valid"]!);
        var diagnostic = bad.Body["diagnostics"]!.AsArray().Single(d => ((string?)d!["key"])?.EndsWith("CacheSeconds") == true)!;
        Assert.EndsWith("server.yaml", (string)diagnostic["file"]!);
        Assert.Equal(draft.Split('\n').ToList().FindIndex(l => l.Contains("cacheSeconds")) + 1, (int)diagnostic["line"]!);
        var put = await SettingsAsync(admin, HttpMethod.Put, "", new { text = draft, expected_version = version });
        Assert.Equal(HttpStatusCode.BadRequest, put.Status);
        Assert.False((bool)put.Body["detail"]!["valid"]!);
        Assert.Contains("nothing was written", (string)put.Body["error"]!);

        // a file that does not parse is said in the reader's words
        var unparsable = await SettingsAsync(admin, HttpMethod.Post, "/check", new { text = "listen:\n  port: [1\n" });
        Assert.False((bool)unparsable.Body["valid"]!);
        Assert.Contains("server.yaml", (string)unparsable.Body["diagnostics"]![0]!["message"]!);

        // the instance id and the paired clients are the pairing's: a change is refused, in the check and in the write
        var moved = text.Replace(System.Text.RegularExpressions.Regex.Match(text, @"instanceId:\s*\S+").Value, "instanceId: someone-else");
        Assert.NotEqual(text, moved);
        Assert.Contains("kept by the pairing commands", (await SettingsAsync(admin, HttpMethod.Post, "/check", new { text = moved })).Body["problems"]!.ToJsonString());
        Assert.Equal(HttpStatusCode.BadRequest, (await SettingsAsync(admin, HttpMethod.Put, "", new { text = moved, expected_version = version })).Status);

        // an empty file and a body with no text are refused
        Assert.Equal(HttpStatusCode.BadRequest, (await SettingsAsync(admin, HttpMethod.Put, "", new { text = "  ", expected_version = version })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SettingsAsync(admin, HttpMethod.Put, "", new { expected_version = version })).Status);

        Assert.Equal(before, File.ReadAllText(ServerFile));
        Assert.Equal(history, new GitCommitService(ConfigDirectory).GetHistory("", 50).Count);
    }

    [Fact]
    public async Task A_write_names_the_version_it_read_and_says_which_changes_need_a_restart_and_an_unchanged_text_makes_no_commit()
    {
        await StartAsync();
        var admin = await PairAsync();
        var read = await SettingsAsync(admin, HttpMethod.Get, "");
        var text = ((string)read.Body["text"]!).TrimEnd();
        var version = (string)read.Body["version"]!;

        // someone else changed the file since it was read (by hand, say): the write is refused with the version it has now
        File.AppendAllText(ServerFile, "# edited by hand\n");
        var stale = await SettingsAsync(admin, HttpMethod.Put, "", new { text = text + "\nlogSql: false\n", expected_version = version });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal("version_conflict", (string)stale.Body["code"]!);
        var current = (string)stale.Body["current_version"]!;
        Assert.NotEqual(version, current);
        Assert.DoesNotContain("logSql", File.ReadAllText(ServerFile));

        // read again, change the views module's cache: a key a running Gateway reads at start, so the answer says a restart is needed
        read = await SettingsAsync(admin, HttpMethod.Get, "");
        Assert.Equal(current, (string)read.Body["version"]!);
        var put = await SettingsAsync(admin, HttpMethod.Put, "", new { text = ((string)read.Body["text"]!).TrimEnd() + "\nviews: { cacheSeconds: 45 }\n", expected_version = current });
        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.True((bool)put.Body["restart_needed"]!);
        Assert.Equal(["views"], put.Body["restart_keys"]!.AsArray().Select(k => (string)k!).ToArray());

        // the same text again: nothing is written and nothing is committed
        var history = new GitCommitService(ConfigDirectory).GetHistory("", 50).Count;
        var again = await SettingsAsync(admin, HttpMethod.Get, "");
        var same = await SettingsAsync(admin, HttpMethod.Put, "", new { text = (string)again.Body["text"]!, expected_version = (string)again.Body["version"]! });
        Assert.Equal(HttpStatusCode.OK, same.Status);
        Assert.Empty(same.Body["changed"]!.AsArray());
        Assert.Equal(history, new GitCommitService(ConfigDirectory).GetHistory("", 50).Count);
    }

    [Fact]
    public async Task A_field_of_the_draft_is_edited_by_the_yaml_editor_keeping_comments_and_the_mask_and_a_bad_path_is_refused()
    {
        await StartAsync();
        var admin = await PairAsync();
        var text = (string)(await SettingsAsync(admin, HttpMethod.Get, "")).Body["text"]! + "\n# keep this comment\nviews:\n  cacheSeconds: 30\n";
        var edited = await SettingsAsync(admin, HttpMethod.Post, "/edit", new { text, set = new Dictionary<string, object> { ["views.cacheSeconds"] = 90, ["views.export.mode"] = "system", ["logSql"] = false }, list = new Dictionary<string, string[]> { ["modules"] = ["flightsql", "views"] } });
        Assert.True(edited.Status == HttpStatusCode.OK, edited.Body.ToJsonString());
        var result = (string)edited.Body["text"]!;
        Assert.Contains("# keep this comment", result);
        Assert.Contains("cacheSeconds: 90", result);
        Assert.Contains("mode: system", result);
        Assert.Contains("logSql: false", result);
        Assert.Contains("modules: [flightsql, views]", result);
        Assert.Contains("certificatePassword: '********'", result);
        Assert.DoesNotContain(TlsSecret, edited.Body.ToJsonString());

        var removed = await SettingsAsync(admin, HttpMethod.Post, "/edit", new { text = result, remove = new[] { "views.export.mode", "nothing.here" } });
        Assert.DoesNotContain("mode: system", (string)removed.Body["text"]!);
        // nothing is written by an edit
        Assert.DoesNotContain("logSql", File.ReadAllText(ServerFile));
        Assert.Equal(HttpStatusCode.BadRequest, (await SettingsAsync(admin, HttpMethod.Post, "/edit", new { text, set = new Dictionary<string, object> { ["not a path"] = 1 } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SettingsAsync(admin, HttpMethod.Post, "/edit", new { text, list = new Dictionary<string, string[]> { ["modules"] = ["has space"] } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SettingsAsync(admin, HttpMethod.Post, "/edit", new { set = new Dictionary<string, object> { ["logSql"] = true } })).Status);
    }

    [Fact]
    public void The_changed_keys_of_two_texts_ignore_comments_and_blank_lines_and_name_each_key_once()
    {
        const string before = "# note\nschemaVersion: 2\n\nmodules: [flightsql]\ntls:\n  mode: selfSigned\n";
        Assert.Empty(GatewayServerSettings.ChangedKeys(before, "# another note\nschemaVersion: 2\nmodules: [flightsql]\n\ntls:\n  # why\n  mode: selfSigned\n"));
        Assert.Equal(["modules", "tls", "logSql"], GatewayServerSettings.ChangedKeys(before, "schemaVersion: 2\nmodules: [flightsql, views]\ntls:\n  mode: none\nlogSql: false\n"));
        Assert.Equal(["tls"], GatewayServerSettings.ChangedKeys(before, "schemaVersion: 2\nmodules: [flightsql]\n"));
    }

    [Fact]
    public void Secrets_are_masked_in_keyed_values_and_in_connection_strings_but_a_reference_to_one_is_not()
    {
        var text = "tls: { mode: selfSigned, certificatePassword: hunter2 }\nidentity:\n  entra: { clientSecret: 'secret:dbdatakit:gateway:x:entra' }\nstate:\n  connection: 'Server=s;User Id=u;Password=p4ss;Encrypt=True'\nother: { apiKey: \"k-123\" }\n";
        var masked = GatewayServerSettings.MaskSecrets(text);
        Assert.Contains("certificatePassword: '********'", masked);
        Assert.Contains("'secret:dbdatakit:gateway:x:entra'", masked);
        Assert.Contains("Password=********;Encrypt=True", masked);
        Assert.Contains("apiKey: '********'", masked);
        Assert.DoesNotContain("hunter2", masked);
        Assert.DoesNotContain("p4ss", masked);
        Assert.DoesNotContain("k-123", masked);
        Assert.Equal(masked, GatewayServerSettings.MaskSecrets(masked));
    }
}
