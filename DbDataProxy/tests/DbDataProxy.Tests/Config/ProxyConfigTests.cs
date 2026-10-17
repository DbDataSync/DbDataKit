using DbDataKit.Config;
using DbDataKit.Config.Testing;
using DbDataKit.Config.Yaml;
using DbDataProxy.Data;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;

namespace DbDataProxy.Tests.Config;

/// <summary>The golden configuration corpus: every valid shape validates, every invalid class fails with the key path and a precise message.</summary>
public sealed class GoldenCorpusTests
{
    public static IEnumerable<object[]> Valid() => ConfigTestSupport.GoldenCases("valid-");
    public static IEnumerable<object[]> Invalid() => ConfigTestSupport.GoldenCases("invalid-");

    [Fact]
    public void Corpus_is_present() => Assert.True(ConfigTestSupport.GoldenCases("").Count() >= 30);

    [Theory]
    [MemberData(nameof(Valid))]
    public void Valid_files_validate(string name)
    {
        var load = ConfigTestSupport.LoadFile(Path.Combine(ConfigTestSupport.GoldenDirectory, name + ".json"));
        Assert.True(load.Ok, ConfigTestSupport.Format(load));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_files_fail_with_the_expected_messages(string name)
    {
        var load = ConfigTestSupport.LoadFile(Path.Combine(ConfigTestSupport.GoldenDirectory, name + ".json"));
        string text = ConfigTestSupport.Format(load);
        Assert.False(load.Ok, "expected errors, got:\n" + text);
        foreach (var line in File.ReadAllLines(Path.Combine(ConfigTestSupport.GoldenDirectory, name + ".expected")).Where(l => l.Length > 0))
            Assert.True(text.Contains(line, StringComparison.Ordinal), $"'{line}' not found in:\n{text}");
        Assert.Throws<ProxyConfigException>(() => load.ValueOrThrow());
    }

    [Fact]
    public void Unknown_keys_name_the_file_and_line()
    {
        var load = ConfigTestSupport.LoadFile(Path.Combine(ConfigTestSupport.GoldenDirectory, "invalid-unknown-key.json"));
        var d = Assert.Single(load.Diagnostics, x => x.Code == "DBP-104");
        Assert.EndsWith("invalid-unknown-key.json", d.Path);
        Assert.Equal(3, d.Line);
        Assert.Equal("Listen.Prot", d.KeyPath);
    }

    [Fact]
    public void Every_problem_is_reported_in_one_pass()
    {
        var load = ConfigTestSupport.LoadFile(Path.Combine(ConfigTestSupport.GoldenDirectory, "invalid-multiple-errors-reported-together.json"));
        Assert.True(load.Diagnostics.Count(d => d.Severity == ConfigSeverity.Error) >= 2);
    }

    [Fact]
    public void Unknown_keys_are_tolerated_only_when_asked_for()
    {
        const string json = """{ "Proxy": { "Listen": { "Prot": 1, "Port": 9100 }, "FromTheFuture": true } }""";
        Assert.False(ConfigTestSupport.LoadJson(json).Ok);

        foreach (var load in new[]
        {
            ConfigTestSupport.LoadJson(json, new ProxyConfigLoadOptions { AllowUnknownKeys = true }),
            ConfigTestSupport.LoadJson("""{ "Proxy": { "AllowUnknownKeys": true, "FromTheFuture": 1, "Listen": { "Prot": 1 } } }"""),
        })
        {
            Assert.True(load.Ok, ConfigTestSupport.Format(load));
            Assert.Contains(load.Diagnostics, d => d.Code == "DBP-104" && d.Severity == ConfigSeverity.Warning);
        }
    }

    [Fact]
    public void Errors_never_print_secret_values()
    {
        const string secret = "TOPSECRET-VALUE-91c";
        var load = ConfigTestSupport.LoadJson($$"""{ "Proxy": { "Tls": { "Mode": "File", "CertificatePassword": "{{secret}}" }, "Auth": { "Jwt": { "SigningKey": "{{secret}}" } }, "Bogus": "{{secret}}" } }""");
        Assert.False(load.Ok);
        Assert.DoesNotContain(secret, ConfigTestSupport.Format(load));
    }
}

public sealed class ShippedFilesTests
{
    private static string Shipped(string name) => Path.Combine(AppContext.BaseDirectory, name);

    [Fact]
    public void Shipped_appsettings_json_validates_with_schema_2()
    {
        var load = ConfigTestSupport.LoadFile(Shipped("appsettings.json"));
        Assert.True(load.Ok, ConfigTestSupport.Format(load));
        Assert.Equal(2, load.Value!.SchemaVersion);
        Assert.Empty(load.Diagnostics);
    }

    [Fact]
    public void Shipped_example_validates_comments_trailing_commas_and_all()
    {
        var load = ConfigTestSupport.LoadFile(Shipped("appsettings.example.json"));
        Assert.True(load.Ok, ConfigTestSupport.Format(load));
        var o = load.Value!;
        Assert.Equal(2, o.SchemaVersion);
        Assert.Equal(["erp", "lake", "legacy", "scratch", "warehouse"], o.DataSources.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("x86", o.DataSources["legacy"].Bitness);
        Assert.Equal(ClassifierMode.Strict, o.DataSources["erp"].Classifier);
        Assert.Equal(20, o.DataSources["erp"].MaxConcurrentQueries);
        Assert.Equal([@"C:\data\parquet"], o.DataSources["lake"].AllowedDirectories);
        Assert.Equal(@"Data Source=C:\data\scratch.duckdb", o.DataSources["scratch"].ConnectionString);
        Assert.Equal("reporting", Assert.Single(o.Users).Name);
    }

    [Fact]
    public void Existing_pascal_case_files_without_a_version_keep_working_unchanged()
    {
        // A file as written by any earlier release: PascalCase keys, no SchemaVersion.
        var load = ConfigTestSupport.LoadJson("""
            { "Proxy": {
                "Listen": { "Address": "0.0.0.0", "Port": 8815 },
                "Tls": { "Mode": "SelfSigned", "Hostnames": [ "a.example.com" ] },
                "Compression": { "Mode": "Arrow", "Codec": "zstd", "Level": 3, "AllowClientOverride": true },
                "Auth": { "SessionIdleMinutes": 30, "SessionMaxHours": 12, "AllowPassThrough": true },
                "DataSources": { "erp": { "Provider": "sqlserver", "ConnectionString": "Server=a;Password=env:P", "CommandTimeoutSeconds": 300 } },
                "Users": [ { "Name": "reporting", "PasswordHash": "PBKDF2-SHA256$1$a$b", "DataSources": [ "erp" ], "DefaultDataSource": "erp" } ]
            } }
            """);
        Assert.True(load.Ok, ConfigTestSupport.Format(load));
        Assert.Equal(1, load.Value!.SchemaVersion);
        Assert.Equal(ClassifierMode.Warn, load.Value.DataSources["erp"].Classifier);
        Assert.Equal(3, load.Value.Compression.Level);
        Assert.Equal(TlsMode.SelfSigned, load.Value.Tls.Mode);
    }
}

public sealed class CaseInsensitiveBindingTests
{
    [Fact]
    public void Keys_and_values_match_ignoring_case_and_booleans_may_be_capitalised()
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["proxy:LISTEN:port"] = "9100",
            ["PROXY:auth:allowpassthrough"] = "False",   // the stock provider spelled booleans like this
            ["Proxy:LogSql"] = "TRUE",
            ["Proxy:DataSources:Erp:PROVIDER"] = "DuckDb",
            ["Proxy:DataSources:Erp:ConnectionString"] = "Data Source=:memory:",
        }).Build();
        var load = ProxyConfig.Load(root);
        Assert.True(load.Ok, ConfigTestSupport.Format(load));
        Assert.Equal(9100, load.Value!.Listen.Port);
        Assert.False(load.Value.Auth.AllowPassThrough);
        Assert.True(load.Value.LogSql);
        Assert.True(load.Value.DataSources.ContainsKey("erp"));   // lookups stay case-insensitive
        Assert.Equal("memory", load.Detail.Merged.Origins().First(o => o.Path == "Listen.Port").File);
    }

    [Fact]
    public void Later_sources_win_and_the_origin_says_which()
    {
        using var file = TempFile.Create("""{ "Proxy": { "Listen": { "Port": 9001, "Address": "127.0.0.1" } } }""");
        var root = new ConfigurationBuilder()
            .AddJsonFile(file.Path, false, false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Proxy:Listen:Port"] = "9002" })
            .Build();
        var load = ProxyConfig.Load(root);
        Assert.Equal(9002, load.Value!.Listen.Port);
        Assert.Equal("127.0.0.1", load.Value.Listen.Address);
        var origins = ProxyConfig.Show(load).ToDictionary(e => e.Path);
        Assert.Equal("memory", origins["Listen.Port"].Source);
        Assert.Equal(file.Path, origins["Listen.Address"].Source);
        Assert.Equal(1, origins["Listen.Address"].Line);
    }
}

public sealed class SchemaVersionTests
{
    private const string Sources = """
        "DataSources": {
          "ro": { "Provider": "duckdb", "ConnectionString": "x" },
          "ro_warn": { "Provider": "duckdb", "ConnectionString": "x", "Classifier": "warn" },
          "rw": { "Provider": "duckdb", "ConnectionString": "x", "AllowWrites": true }
        }
        """;

    [Fact]
    public void No_version_means_1_and_keeps_the_warn_default_with_a_hint()
    {
        var load = ConfigTestSupport.LoadJson("{ \"Proxy\": { " + Sources + " } }");
        Assert.True(load.Ok);
        Assert.Equal(1, load.Value!.SchemaVersion);
        Assert.Equal(ClassifierMode.Warn, load.Value.DataSources["ro"].Classifier);
        var hint = Assert.Single(load.Diagnostics, d => d.Severity == ConfigSeverity.Warning);
        Assert.Contains("ro", hint.Message);
        Assert.DoesNotContain("ro_warn", hint.Message);   // explicit settings are not nagged
        Assert.DoesNotContain("rw", hint.Message);        // writable sources are not classified
    }

    [Fact]
    public void Version_2_defaults_the_classifier_to_strict_but_honours_explicit_values()
    {
        var load = ConfigTestSupport.LoadJson("{ \"Proxy\": { \"SchemaVersion\": 2, " + Sources + " } }");
        Assert.True(load.Ok, ConfigTestSupport.Format(load));
        Assert.Equal(ClassifierMode.Strict, load.Value!.DataSources["ro"].Classifier);
        Assert.Equal(ClassifierMode.Warn, load.Value.DataSources["ro_warn"].Classifier);
        Assert.Empty(load.Diagnostics);
    }

    [Fact]
    public void Explicit_version_1_is_the_same_as_absent_and_the_key_is_case_insensitive()
    {
        var root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Proxy:schemaversion"] = "1",
            ["Proxy:DataSources:ro:Provider"] = "duckdb",
            ["Proxy:DataSources:ro:ConnectionString"] = "x",
        }).Build();
        var load = ProxyConfig.Load(root);
        Assert.Equal(ClassifierMode.Warn, load.Value!.DataSources["ro"].Classifier);

        root = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Proxy:SCHEMAVERSION"] = "2",
            ["Proxy:DataSources:ro:Provider"] = "duckdb",
            ["Proxy:DataSources:ro:ConnectionString"] = "x",
        }).Build();
        Assert.Equal(ClassifierMode.Strict, ProxyConfig.Load(root).Value!.DataSources["ro"].Classifier);
    }

    [Fact]
    public void A_newer_version_is_refused_with_an_upgrade_message_and_nothing_else()
    {
        var load = ConfigTestSupport.LoadJson("""{ "Proxy": { "SchemaVersion": 99, "Nonsense": 1 } }""");
        Assert.False(load.Ok);
        var d = Assert.Single(load.Diagnostics);
        Assert.Equal("DBP-110", d.Code);
        Assert.Contains("Upgrade DbDataProxy", d.Fix);
    }
}

public sealed class MigrationTests
{
    private const string V1 = """
        # proxy settings
        Listen:
          Address: 127.0.0.1
          Port: 9000
        Users: []
        """;

    [Fact]
    public void Versions_agree_between_the_schema_the_engine_and_the_constant()
    {
        Assert.Equal(ProxyConfig.LatestSchemaVersion, SchemaModel.ForObject<ProxyOptions>().Version);
        Assert.Equal(ProxyConfig.LatestSchemaVersion, ProxyConfig.Migrations.LatestVersion(ProxyConfig.Kind));
        Assert.Equal(ProxyConfig.Kind, SchemaModel.ForObject<ProxyOptions>().Kind);
    }

    [Fact]
    public void V1_to_v2_is_a_documented_no_op_that_only_records_the_version()
    {
        var plan = ProxyConfig.Migrations.Plan(ProxyConfig.Kind, V1, "proxy.yaml");
        Assert.Equal(MigrationStatus.Migrated, plan.Status);
        Assert.Equal(1, plan.FromVersion);
        Assert.Equal(2, plan.ToVersion);
        var v1 = V1.Replace("\r\n", "\n"); // the checkout may have CRLF line endings
        Assert.Equal(v1.Replace("# proxy settings\n", "# proxy settings\nschemaVersion: 2\n"), plan.NewText.Replace("\r\n", "\n"));
        Assert.Contains(plan.Log, l => l.Contains("No keys change"));
        Assert.Equal(MigrationStatus.UpToDate, ProxyConfig.Migrations.Plan(ProxyConfig.Kind, plan.NewText, "proxy.yaml").Status);
    }

    /// <summary>How a future rename ships: one more IConfigMigration (here test-only: Listen.Address becomes Listen.Host in schema 3).</summary>
    private sealed class RenameListenAddress : IConfigMigration
    {
        public string Kind => ProxyConfig.Kind;
        public int From => 2;
        public int To => 3;
        public void Apply(YamlDocument doc, IMigrationLog log)
        {
            if (doc.Has("Listen.Address")) doc.RenameKey("Listen.Address", "Host");
            log.Info("Listen.Address is now Listen.Host");
        }
    }

    [Fact]
    public void A_future_rename_migrates_a_v1_file_through_the_no_op_step_and_keeps_comments()
    {
        var engine = ProxyConfigMigrations.CreateEngine(new RenameListenAddress());
        Assert.Equal(3, engine.LatestVersion(ProxyConfig.Kind));

        var plan = engine.Plan(ProxyConfig.Kind, V1, "proxy.yaml");
        Assert.Equal(MigrationStatus.Migrated, plan.Status);
        Assert.Equal(3, plan.ToVersion);
        string text = plan.NewText.Replace("\r\n", "\n");
        Assert.Contains("# proxy settings", text);
        Assert.Contains("schemaVersion: 3", text);
        Assert.Contains("  Host: 127.0.0.1", text);
        Assert.DoesNotContain("Address:", text);
        Assert.Equal(["Proxy: 1 -> 2".Replace("Proxy", ProxyConfig.Kind), $"{ProxyConfig.Kind}: 2 -> 3"], plan.Log.Where(l => l.Contains("->")));
        Assert.Contains("Listen.Address is now Listen.Host", plan.Log);

        // already migrated files are left alone
        Assert.Equal(MigrationStatus.UpToDate, engine.Plan(ProxyConfig.Kind, plan.NewText, "proxy.yaml").Status);
    }

    [Fact]
    public void The_shipped_engine_refuses_a_file_from_a_newer_schema()
    {
        var plan = ProxyConfig.Migrations.Plan(ProxyConfig.Kind, "schemaVersion: 3\nListen:\n  Port: 1\n", "proxy.yaml");
        Assert.Equal(MigrationStatus.Refused, plan.Status);
        Assert.Contains(plan.Diagnostics, d => d.Code == "DBD-110");
    }

    [Fact]
    public void The_binder_schema_and_the_json_schema_agree()
    {
        SchemaAgreement.Assert<ProxyOptions>();
        Assert.Contains("SchemaVersion", JsonSchemaGenerator.ToJson(typeof(ProxyOptions)), StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ProviderTableTests
{
    [Fact]
    public void Known_and_pass_through_provider_lists_match_the_registered_providers()
    {
        var registry = Support.TestSources.Registry();
        Assert.Equal(ProxyConfigValidator.KnownProviders.Order(), registry.Engines.Order());

        var passThrough = ProxyConfigValidator.KnownProviders.Where(name =>
        {
            try { registry.Get(name).Composer.ApplyPassThroughCredentials(name == "odbc" ? "Driver={X};Server=a" : "Server=a;Database=b", "u", DbDataKit.Secrets.SecretValue.From("p")); return true; }
            catch (DbDataKit.Connections.ConnectionPolicyException) { return false; }
        });
        Assert.Equal(ProxyConfigValidator.PassThroughProviders.Order(), passThrough.Order());
    }
}
