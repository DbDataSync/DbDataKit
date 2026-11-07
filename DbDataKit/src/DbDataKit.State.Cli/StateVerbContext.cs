using System.Data.Common;
using DbDataKit.Cli;
using DbDataKit.Secrets;

namespace DbDataKit.State.Cli;

/// <summary>Where one product's state database is, as the options of this command line say: its settings, the SQLite file when the engine is SQLite, and how the secret references in its connection string are resolved.</summary>
/// <param name="Settings">The effective settings: what the product opens its state with.</param>
/// <param name="SqlitePath">The file a SQLite state is (the product's directory and <c>state.db</c>); not created by anything here.</param>
/// <param name="Secrets">The resolver for the references in <see cref="StateSettings.ConnectionString"/>, made when it is first needed: <c>state show</c> never asks for it, <c>state test</c> does. Null: references are left as written.</param>
/// <param name="SettingsFile">The settings file whose <c>state:</c> section these settings were read from, and where <c>state move</c> writes the new section (<see cref="StateSettingsWriter"/>). Null: the product keeps no such file, and <c>state move</c> says so and does nothing.</param>
public sealed record StateTarget(StateSettings Settings, string SqlitePath, Func<ISecretResolver?>? Secrets = null, string? SettingsFile = null);

/// <summary>
/// What a product tells the shared <c>state</c> verbs (<see cref="StateVerbs.Create"/>): its program name, how to find its state from the options of the command, the modules it is built from and the scope
/// of the process. A product with no module list yet passes <c>null</c> for <see cref="Modules"/> and the verbs say so.
/// </summary>
/// <param name="Program">The command's name, as an operator types it (<c>dbdatakit</c>): it is in the sentences that name a fix.</param>
/// <param name="Product">What the state belongs to, for a heading: <c>the console</c>, <c>the proxy</c>.</param>
/// <param name="Locate">Reads the product's own sources (a settings file, the environment, <c>--repo</c>) into a <see cref="StateTarget"/>. May throw <see cref="InvalidOperationException"/> or <see cref="ArgumentException"/> with a message naming what is wrong.</param>
/// <param name="Modules">The modules the product's state is built from; null when the product has none listed yet.</param>
/// <param name="Scope">The group and service this process is.</param>
/// <param name="Options">The valued options <paramref name="Locate"/> reads (<c>repo</c>, <c>engine</c>), all of them accepted by every verb.</param>
/// <param name="OptionsUsage">How those options are written in a usage line: <c>[--repo &lt;console directory&gt;]</c>.</param>
public sealed record StateVerbContext(
    string Program,
    string Product,
    Func<CliArgs, StateTarget> Locate,
    IReadOnlyList<StateModule>? Modules,
    StateScope Scope,
    IReadOnlyList<string> Options,
    string OptionsUsage)
{
    /// <summary>For tests: what the engine's version is, in place of asking the engine (the support check of <c>state test</c> is run against it).</summary>
    public Func<DbConnection?, string?>? EngineVersion { get; init; }

    /// <summary>For tests: changes the options of <c>state move</c> before it runs (for example <c>MoveOptions.AfterImport</c>, to inject a failure after the import, or a fixed snapshot directory).</summary>
    public Func<DbDataKit.State.Engines.Snapshots.MoveOptions, DbDataKit.State.Engines.Snapshots.MoveOptions>? ConfigureMove { get; init; }
}

/// <summary>The exit codes of <c>state show</c> and <c>state test</c>: 0 is everything fine, 2 a wrong use (as everywhere); every other code is one kind of failure, so a script can tell them apart.</summary>
public static class StateExit
{
    /// <summary>The settings cannot be used: an engine that is not one of the three, a server engine without a connection string, a secret reference nobody has, a namespace setting that is not valid.</summary>
    public const int Settings = 10;

    /// <summary>The server cannot be reached, or the database named is not there.</summary>
    public const int Unreachable = 11;

    /// <summary>The server refused the login: a wrong password or an unknown login.</summary>
    public const int LoginRefused = 12;

    /// <summary>The login cannot read the registry, or see it and cannot create it.</summary>
    public const int Registry = 13;

    /// <summary>The login lacks rights the service needs to run (or the SQLite file or its folder is not writable).</summary>
    public const int Rights = 14;

    /// <summary>The engine is older than the oldest supported.</summary>
    public const int EngineVersion = 15;

    /// <summary>A table of the layout already exists and is not one of this module's.</summary>
    public const int SchemaClash = 16;

    /// <summary>Migrations are pending and this login may not apply them.</summary>
    public const int PendingBlocked = 17;

    /// <summary>The service would refuse the database: made by the old structure, a module newer than the binary, a namespace that moved, an edited migration.</summary>
    public const int Refused = 18;

    // The snapshot verbs (export, import, verify, move; plan 29b X04) use 20 to 29, so the codes of show and test keep their numbers.

    /// <summary>An export was refused: a live instance uses the database (stop the service), the SQLite file is held by another process, or the snapshot directory is not empty.</summary>
    public const int SnapshotRefused = 20;

    /// <summary>An import or a move was refused because the target already holds modules: it goes into an empty database.</summary>
    public const int TargetNotEmpty = 21;

    /// <summary>The snapshot cannot be used: no manifest, another format, a damaged or missing file, a module newer than this software.</summary>
    public const int SnapshotInvalid = 22;

    /// <summary><c>state verify</c> found a difference (the sentence and the list name the table and the rows), or a move's verification did.</summary>
    public const int VerifyDiffers = 23;

    /// <summary>The database is not ready to be read: empty, not a state database, or older than this software (start the product once so it migrates it).</summary>
    public const int NotReady = 24;

    /// <summary><c>state move</c> stopped at a step; the settings file was not changed and the old database was not touched.</summary>
    public const int MoveFailed = 25;

    /// <summary><c>state move</c> was not confirmed: it needs <c>--yes</c> or an answer of yes.</summary>
    public const int NotConfirmed = 26;

    /// <summary><c>--include-secrets-check</c> found a secret of the connection string in the exported snapshot (the snapshot is removed).</summary>
    public const int SecretInSnapshot = 27;
}
