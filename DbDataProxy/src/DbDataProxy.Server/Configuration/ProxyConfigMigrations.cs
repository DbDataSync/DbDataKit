using DbDataKit.Config;

namespace DbDataProxy.Server.Configuration;

/// <summary>
/// The configuration migrations of DbDataProxy, run by the shared <see cref="MigrationEngine"/> (kind <see cref="ProxyConfig.Kind"/>).
/// 1 to 2 is a documented no-op: it changes no key. Schema 2 only changes defaults (the statement <c>Classifier</c> of a data source defaults to
/// <c>strict</c> instead of <c>warn</c>), so the step just records the new version after the operator reviewed the classifier setting.
/// A future key rename is one more <see cref="IConfigMigration"/> that calls <c>doc.RenameKey(...)</c>; the migration tests show a worked example.
/// The engine edits YAML documents with a top-level version key, so it cannot rewrite <c>appsettings.json</c> (comments, nested section) in place:
/// the server never auto-migrates, and a file of an older schema stays valid.
/// </summary>
public static class ProxyConfigMigrations
{
    public static MigrationEngine CreateEngine(params IConfigMigration[] additional) =>
        new([new V1ToV2(), .. additional]);

    private sealed class V1ToV2 : IConfigMigration
    {
        public string Kind => ProxyConfig.Kind;
        public int From => 1;
        public int To => 2;

        public void Apply(YamlDocument doc, IMigrationLog log) =>
            log.Info("No keys change. Schema 2 makes each data source's Classifier default to strict; set Classifier explicitly to keep warn or off.");
    }
}
