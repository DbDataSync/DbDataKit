namespace DbDataKit.Hosting.Startup;

/// <summary>
/// The steps of a server's start, in the order they run. A server fills in the steps it has; the order never changes.
/// Design: docs/install.md#one-startup-sequence
/// </summary>
public enum StartupStep
{
    /// <summary>The top folder exists with the owner and access list the install set; the subfolders the server needs are made.</summary>
    Folder,

    /// <summary>The key ring and the secret store are opened or made, as the service's own account.</summary>
    Keys,

    /// <summary>The server's certificate is opened or made.</summary>
    Certificate,

    /// <summary>The state database is opened or made and its migrations run.</summary>
    State,

    /// <summary>Each listener is bound.</summary>
    Ports,

    /// <summary>A waiting join request is consumed and the server joins the console; otherwise the server checks it is joined.</summary>
    Join,
}

public static class StartupSteps
{
    /// <summary>The six steps in the order they run.</summary>
    public static IReadOnlyList<StartupStep> InOrder { get; } = [StartupStep.Folder, StartupStep.Keys, StartupStep.Certificate, StartupStep.State, StartupStep.Ports, StartupStep.Join];

    /// <summary>The step's name as the failure file writes it: <c>folder</c>, <c>keys</c>, <c>certificate</c>, <c>state</c>, <c>ports</c>, <c>join</c>.</summary>
    public static string Name(this StartupStep step) => step switch
    {
        StartupStep.Folder => "folder",
        StartupStep.Keys => "keys",
        StartupStep.Certificate => "certificate",
        StartupStep.State => "state",
        StartupStep.Ports => "ports",
        StartupStep.Join => "join",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    /// <summary>What a person looks at when the step fails and the server could not say more: the failure file adds it under the exception chain.</summary>
    internal static string WhatToCheck(this StartupStep step) => step switch
    {
        StartupStep.Folder => "Check that the account above owns the data folder, or can write it; `dbdatakit setup single-server` sets the folders up.",
        StartupStep.Keys => "Check the key ring and secret store files in the data folder: the account above must be able to open them, and no other account should be granted access.",
        StartupStep.Certificate => "Check the certificate file the server's settings name: the account above must be able to read it and its private key.",
        StartupStep.State => "Check the state database: that the engine answers, that the database exists and that the account the server signs in with may change it.",
        StartupStep.Ports => "Check that no other program holds the server's ports (`netstat -ano` on Windows, `ss -ltnp` on Linux) and that they are outside Windows' excluded port ranges.",
        StartupStep.Join => "Check that the console's address answers from this machine, and that the join command was made for this server.",
        _ => "",
    };
}
