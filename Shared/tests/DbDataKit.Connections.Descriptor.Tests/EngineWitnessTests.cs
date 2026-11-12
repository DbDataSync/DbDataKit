using System.Text.RegularExpressions;
using DbDataKit.DataModel;
using DbDataKit.DataModel.Storage;
using DbDataKit.Semantic.Emit;

namespace DbDataKit.Connections.Descriptor.Tests;

/// <summary>
/// Plan 14a SL01: a role a descriptor calls supported has a witness in code, and a witness in code has a supported role. The roles that need a product
/// the registry does not reference (the Sync and Build roles) are checked by their own products' tests; these three are checked here, so a claim in a
/// descriptor cannot be older than the code again (plan 14a section 1 found three that were).
/// </summary>
public class EngineWitnessTests
{
    private static readonly EngineRegistry Registry = EngineRegistry.Builtin;

    private static void Matches(string role, IEnumerable<string> witnessed, string witness)
    {
        var claimed = Registry.Supporting(role).Select(e => e.Id).Order().ToList();
        var found = witnessed.Order().ToList();
        foreach (var id in found.Except(claimed))
            Assert.Fail($"{id}: the code has {witness}, but the descriptor does not say {EngineRoles.Title(role)} is supported (engines/{id}.engine.yaml).");
        foreach (var id in claimed.Except(found))
            Assert.Fail($"{id}: the descriptor says {EngineRoles.Title(role)} is supported, but the code has no {witness}.");
    }

    [Fact]
    public void SemanticEmitterIsSupportedExactlyWhereTheEmitterWritesTheDialect()
    {
        string[] emitted = [EmitDialects.TSql.Id.Value, EmitDialects.DuckDb.Id.Value, EmitDialects.Postgres.Id.Value];
        Matches(EngineRoles.SemanticEmitter, emitted.Select(d => Registry.Engines.Single(e => e.Dialect == d).Id), "an entry in EmitDialects");
    }

    [Fact]
    public void MasterDataStoreIsSupportedExactlyWhereModelStorageWritesTables()
    {
        var loaded = DataModelLoader.Load("""
            schemaVersion: 1
            name: witness
            title: Witness
            storage: {connection: mdm}
            entities:
              thing:
                key: [code]
                attributes:
                  code: {type: VARCHAR(10), required: true}
            """, "witness.yaml");
        Assert.True(loaded.Ok);
        var stored = new List<string>();
        foreach (var engine in Registry.Engines)
        {
            try { ModelStorage.Plan(loaded.Model!, engine.Id); stored.Add(engine.Id); }
            catch (StorageException) { }
        }
        Matches(EngineRoles.MasterDataStore, stored, "a storage in DbDataKit.DataModel");
    }

    [Fact]
    public void GatewaySourceIsSupportedExactlyWhereTheGatewayRegistersAProvider()
    {
        var host = FindUp("DbDataGateway/src/DbDataGateway.Server/Data/GatewaySources.cs");
        if (host is null) return;   // a source package without the repository around it
        var kinds = Regex.Matches(File.ReadAllText(host), @"new DbDataKit\.Connections\.(\w+)\.\1Provider\(").Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();
        // FlightSql is a provider too, but a gateway reaching another gateway is not what the role means (descriptor note)
        kinds.Remove("flightsql");
        Assert.NotEmpty(kinds);
        Matches(EngineRoles.GatewaySource, kinds.Select(k => Registry.Engines.Single(e => e.Provider == k || e.Id == k).Id), "a provider registered by GatewaySources");
    }

    private static string? FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
