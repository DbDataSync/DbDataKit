using System.Data.Common;

namespace DbDataKit.State;

/// <summary>
/// The <c>deploys</c> module: the record of each deploy of a build, kept by the control service that made it. One row per item of a build sent to a Runner or a Gateway (a Runner package, a Store model's package, Gateway models, an app), found
/// by the Workspace server, project, workspace and build it came from, with the target, what the target answered (the version and channel), the build record that was signed with it, who deployed it and whether the target took it.
/// A refused deploy is kept too, with the target's words. Kept until an administrator removes it, like the releases.
/// </summary>
public static class DeploysModule
{
    public const string Name = "deploys";

    public static StateModule Module { get; } = new(
        Name,
        StateScopeKind.Install,
        [
            """
            -- One deploy of one item of a build. workspace_server is the Workspace server's instance id; item_id says which item of the build went (package, model-package/<model>, model, semantic-model, app);
            -- target is the registered server's instance id and product its product. definition_name, channel, deployed_version and deploy_mode are what the target answered (null when it refused or does not say);
            -- signature is how the target found the signature (verified, unsigned) and signer the key that signed. build_record is the text that was signed with the version. outcome is ok or refused, error the target's words.
            CREATE TABLE {table:deploy} (
                id                {{key}} PRIMARY KEY,
                workspace_server  {{key}} NOT NULL,
                project           {{key}} NOT NULL,
                workspace         {{key}} NOT NULL,
                build_id          {{key}} NOT NULL,
                item_id           {{key}} NOT NULL,
                project_kind      {{key}} NOT NULL,
                target            {{key}} NOT NULL,
                target_name       {{key}} NOT NULL,
                product           {{key}} NOT NULL,
                route             {{key}} NOT NULL,
                definition_name   {{key}} NULL,
                channel           {{key}} NULL,
                deployed_version  {{int}} NULL,
                deploy_mode       {{key}} NULL,
                signature         {{key}} NULL,
                signer            {{key}} NULL,
                build_record      {{text}} NOT NULL,
                deployed_by       {{key}} NOT NULL,
                deployed_at       {{time}} NOT NULL,
                outcome           {{key}} NOT NULL,
                error             {{text}} NULL,
                answer_text       {{text}} NULL
            );
            CREATE INDEX {index:deploy_by_build} ON {table:deploy}(workspace_server, project, workspace, build_id);
            """,
        ]);
}

/// <summary>One deploy of one item of a build, as <see cref="DeployStore"/> keeps it (<see cref="DeploysModule"/>).</summary>
public sealed record DeployRecord(string Id, string WorkspaceServer, string Project, string Workspace, string Build, string Item, string ProjectKind, string Target, string TargetName, string Product, string Route,
    string? DefinitionName, string? Channel, int? Version, string? Mode, string? Signature, string? Signer, string BuildRecord, string DeployedBy, DateTimeOffset DeployedAt, string Outcome, string? Error, string? Answer);

/// <summary>The deploys of an install's builds, newest first.</summary>
public sealed class DeployStore(StateDatabase database)
{
    private const string Module = DeploysModule.Name;
    private const string Columns = "id, workspace_server, project, workspace, build_id, item_id, project_kind, target, target_name, product, route, definition_name, channel, deployed_version, deploy_mode, signature, signer, build_record, deployed_by, deployed_at, outcome, error, answer_text";

    /// <summary>Keeps <paramref name="record"/> (its id and time as given; a new id with <see cref="NewId"/>).</summary>
    public DeployRecord Add(DeployRecord record)
    {
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var insert = database.Command(Module, connection, $"INSERT INTO {{table:deploy}} ({Columns}) VALUES ($id, $server, $project, $workspace, $build, $item, $kind, $target, $target_name, $product, $route, $definition, $channel, $version, $mode, $signature, $signer, $record, $by, $at, $outcome, $error, $answer);");
            insert.Bind(database, "id", record.Id).Bind(database, "server", record.WorkspaceServer).Bind(database, "project", record.Project).Bind(database, "workspace", record.Workspace).Bind(database, "build", record.Build)
                .Bind(database, "item", record.Item).Bind(database, "kind", record.ProjectKind).Bind(database, "target", record.Target).Bind(database, "target_name", record.TargetName).Bind(database, "product", record.Product).Bind(database, "route", record.Route)
                .Bind(database, "definition", record.DefinitionName, System.Data.DbType.String).Bind(database, "channel", record.Channel, System.Data.DbType.String).Bind(database, "version", record.Version is { } v ? (long?)v : null, System.Data.DbType.Int64)
                .Bind(database, "mode", record.Mode, System.Data.DbType.String).Bind(database, "signature", record.Signature, System.Data.DbType.String).Bind(database, "signer", record.Signer, System.Data.DbType.String)
                .Bind(database, "record", record.BuildRecord).Bind(database, "by", record.DeployedBy).Bind(database, "at", record.DeployedAt).Bind(database, "outcome", record.Outcome)
                .Bind(database, "error", record.Error, System.Data.DbType.String).Bind(database, "answer", record.Answer, System.Data.DbType.String);
            insert.ExecuteNonQuery();
        });
        return record;
    }

    /// <summary>
    /// Records what became of a deploy after it was kept: the mode the version is in now and the answer with what was done since (a staged version that waited for its preflight and was then activated). The deploy's other columns
    /// are what was sent and stay as they were. False when there is no such deploy.
    /// </summary>
    public bool Revise(string id, string? mode, string? answer) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection, "UPDATE {table:deploy} SET deploy_mode = $mode, answer_text = $answer WHERE id = $id;");
            update.Bind(database, "id", id).Bind(database, "mode", mode, System.Data.DbType.String).Bind(database, "answer", answer, System.Data.DbType.String);
            return update.ExecuteNonQuery() == 1;
        });

    /// <summary>A new id for a deploy, ordered by time.</summary>
    public static string NewId() => Guid.CreateVersion7().ToString("N");

    /// <summary>
    /// The deploys of the builds of a Workspace server, newest first: of one project, one workspace of it and one build when they are named (each narrows the one before), at most <paramref name="limit"/>.
    /// </summary>
    public IReadOnlyList<DeployRecord> List(string workspaceServer, string? project = null, string? workspace = null, string? build = null, int limit = 200) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            var where = "workspace_server = $server" + (project is null ? "" : " AND project = $project") + (project is not null && workspace is not null ? " AND workspace = $workspace" : "")
                + (project is not null && workspace is not null && build is not null ? " AND build_id = $build" : "");
            using var command = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:deploy}} WHERE {where} ORDER BY deployed_at DESC, id DESC;");
            command.Bind(database, "server", workspaceServer);
            if (project is not null) command.Bind(database, "project", project);
            if (project is not null && workspace is not null) command.Bind(database, "workspace", workspace);
            if (project is not null && workspace is not null && build is not null) command.Bind(database, "build", build);
            using var reader = command.ExecuteReader();
            var found = new List<DeployRecord>();
            while (reader.Read() && found.Count < limit) found.Add(Read(reader));
            return (IReadOnlyList<DeployRecord>)found;
        });

    public DeployRecord? Get(string id) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:deploy}} WHERE id = $id;");
            command.Bind(database, "id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        });

    private static DeployRecord Read(DbDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new DeployRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10),
            S(11), S(12), r.NullableInt32(13), S(14), S(15), S(16), r.GetString(17), r.GetString(18), r.Time(19), r.GetString(20), S(21), S(22));
    }
}
