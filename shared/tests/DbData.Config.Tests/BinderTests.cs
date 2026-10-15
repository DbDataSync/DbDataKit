using DbData.Config.Yaml;

namespace DbData.Config.Tests;

public class BinderTests
{
    private const string Good = """
        schemaVersion: 2
        listen: { address: 0.0.0.0, port: 6000 }
        users:
          - name: alice
            dataSources: [erp, crm]
        dataSources:
          erp: { provider: sqlserver, connect_timeout: 20 }
          crm: { provider: postgres }
        timeout: 45
        password: hunter2
        bitness: x86
        idle: 5m
        verbose: true
        tags: [a, b]
        segment: { kind: rows, size: 50 }
        """;

    [Fact]
    public void Binds_a_full_document()
    {
        var r = Util.Bind<ProxyConfig>(Good);
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        var c = r.Value!;
        Assert.Equal(6000, c.Listen.Port);
        Assert.Equal("0.0.0.0", c.Listen.Address);
        Assert.Equal(["erp", "crm"], c.Users[0].DataSources);
        Assert.Equal(20, c.DataSources["erp"].ConnectTimeout);
        Assert.Equal(15, c.DataSources["crm"].ConnectTimeout);
        Assert.Equal(Bitness.X86, c.Bitness);
        Assert.Equal(TimeSpan.FromMinutes(5), c.Idle);
        Assert.True(c.Verbose);
        Assert.Equal(50, Assert.IsType<RowsSegment>(c.Segment).Size);
        Assert.Equal(4, r.Sources["users[0].name"].Line);
    }

    [Fact]
    public void Absent_keys_keep_initializers_and_declared_defaults()
    {
        var r = Util.Bind<ProxyConfig>("verbose: true\n");
        Assert.True(r.Ok);
        Assert.Equal(30, r.Value!.Timeout);
        Assert.Equal("127.0.0.1", r.Value.Listen.Address);
    }

    [Fact]
    public void Empty_document_binds_to_defaults()
    {
        var r = Util.Bind<ProxyConfig>("");
        Assert.True(r.Ok);
        Assert.Equal(30, r.Value!.Timeout);
    }

    [Fact]
    public void Unknown_key_is_an_error_with_did_you_mean_and_position()
    {
        var r = Util.Bind<ProxyConfig>("verbose: true\ntimeuot: 5\n");
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("DBD-104", d.Code);
        Assert.Equal(2, d.Line);
        Assert.Contains("Did you mean `timeout`", d.Message);
    }

    [Fact]
    public void All_problems_are_reported_at_once()
    {
        var r = Util.Bind<ProxyConfig>("timeout: abc\nlisten: { port: 70000 }\nusers:\n  - dataSources: [x]\nbitness: x128\nbogus: 1\n");
        Assert.Equal(5, r.Diagnostics.Count(d => d.Severity == ConfigSeverity.Error));
        Assert.Contains(r.Diagnostics, d => d.Code == "DBD-105" && d.Message.Contains("`name`"));
        Assert.Contains(r.Diagnostics, d => d.Message.Contains("maximum"));
        Assert.Null(r.Value!.Segment);
    }

    [Fact]
    public void Range_pattern_enum_bool_and_duration_are_checked()
    {
        Assert.Contains(Util.Bind<ProxyConfig>("timeout: 0\n").Diagnostics, d => d.Message.Contains("minimum"));
        Assert.Contains(Util.Bind<ProxyConfig>("dataSources: { a: { provider: SQL } }\n").Diagnostics, d => d.Message.Contains("does not match"));
        Assert.Contains(Util.Bind<ProxyConfig>("verbose: yes\n").Diagnostics, d => d.Code == "DBD-106");
        Assert.Contains(Util.Bind<ProxyConfig>("idle: soon\n").Diagnostics, d => d.Code == "DBD-106");
        Assert.Contains(Util.Bind<ProxyConfig>("timeout: 99999999999999999999\n").Diagnostics, d => d.Code == "DBD-106");
        Assert.True(Util.Bind<ProxyConfig>("idle: 00:01:30\n").Ok);
        Assert.True(Util.Bind<ProxyConfig>("bitness: X64\n").Ok);
    }

    [Fact]
    public void Wrong_shapes_are_diagnostics()
    {
        Assert.Contains(Util.Bind<ProxyConfig>("tags: x\n").Diagnostics, d => d.Message.Contains("must be a list"));
        Assert.Contains(Util.Bind<ProxyConfig>("listen: x\n").Diagnostics, d => d.Message.Contains("must be a mapping"));
        Assert.Contains(Util.Bind<ProxyConfig>("timeout: [1]\n").Diagnostics, d => d.Message.Contains("single value"));
    }

    [Fact]
    public void Null_scalar_is_ok_for_nullable_and_an_error_otherwise()
    {
        Assert.True(Util.Bind<ProxyConfig>("idle:\n").Ok);
        Assert.False(Util.Bind<ProxyConfig>("timeout:\n").Ok);
    }

    [Fact]
    public void Deprecated_key_warns_but_binds()
    {
        var r = Util.Bind<ProxyConfig>("oldTimeout: 5\n");
        Assert.True(r.Ok);
        Assert.Equal(5, r.Value!.OldTimeout);
        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(ConfigSeverity.Warning, d.Severity);
        Assert.Contains("since 2", d.Message);
    }

    [Fact]
    public void Union_picks_the_case_by_discriminator()
    {
        var r = Util.Bind<ProxyConfig>("segment: { kind: time, column: ts }\n");
        Assert.Equal("ts", Assert.IsType<TimeSegment>(r.Value!.Segment).Column);
        var bad = Util.Bind<ProxyConfig>("segment: { kind: rowz }\n");
        var d = Assert.Single(bad.Diagnostics);
        Assert.Equal("DBD-107", d.Code);
        Assert.Contains("Did you mean `rows`", d.Message);
        Assert.Contains(Util.Bind<ProxyConfig>("segment: { size: 1 }\n").Diagnostics, d => d.Code == "DBD-105");
        Assert.Contains(Util.Bind<ProxyConfig>("segment: { kind: rows, column: x }\n").Diagnostics, d => d.Code == "DBD-104");
    }

    [Fact]
    public void Preserve_policy_keeps_unknown_keys_and_never_errors()
    {
        var r = Util.Bind<LooseConfig>("name: n\nfuture: {a: 1}\nother: x\n");
        Assert.True(r.Ok);
        Assert.Equal(["future", "other"], r.Value!.Extra.Keys.Order());
    }

    [Fact]
    public void Key_naming_policies()
    {
        Assert.Equal("connect_timeout", SchemaModel.ForObject<DataSourceConfig>().Properties[1].Key);
        Assert.Equal("dataSources", SchemaModel.ForObject<UserConfig>().Properties[1].Key);
        Assert.Equal("ConnectTimeout", SchemaModel.ApplyNaming("ConnectTimeout", KeyNaming.Verbatim));
    }

    [Fact]
    public void Validators_see_the_whole_document_and_report_at_the_right_line()
    {
        var yaml = "users:\n  - name: a\n    dataSources: [missing]\ndataSources:\n  erp: { provider: x }\n";
        var r = Util.Bind<ProxyConfig>(yaml);
        var diags = ConfigValidationContext.Run(r.Value!, r.Sources, "t.yml", [new CrossRef()]);
        var d = Assert.Single(diags);
        Assert.Equal(3, d.Line);
        Assert.Contains("missing", d.Message);
    }

    private sealed class CrossRef : IConfigValidator<ProxyConfig>
    {
        public void Validate(ProxyConfig v, IConfigValidationContext cx)
        {
            for (var i = 0; i < v.Users.Count; i++)
                for (var j = 0; j < v.Users[i].DataSources.Count; j++)
                    if (!v.DataSources.ContainsKey(v.Users[i].DataSources[j]))
                        cx.Error($"users[{i}].dataSources[{j}]", $"Data source `{v.Users[i].DataSources[j]}` is not defined.");
        }
    }

    [Fact]
    public void Types_must_have_parameterless_constructors()
    {
        Assert.Throws<InvalidOperationException>(() => SchemaModel.For(typeof(NoCtor)));
    }

    private sealed class NoCtor { public NoCtor(int x) { } }
}
