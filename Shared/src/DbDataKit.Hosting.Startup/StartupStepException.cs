namespace DbDataKit.Hosting.Startup;

/// <summary>One fact a failed step names in the failure file: <c>Port: 7821</c>.</summary>
public sealed record StartupFact(string Label, string Value);

/// <summary>
/// A step that failed for a reason the server can name. Each step has its own factory, so a failure of the same step says the same things in every
/// product: the folder step names the folder, its owner and the account; the keys step the key file and the grant; and so on. A step may throw any
/// other exception as well; the failure file then names the step and the exception chain.
/// Design: docs/install.md#one-startup-sequence
/// </summary>
public sealed class StartupStepException : Exception
{
    private StartupStepException(StartupStep step, string message, IReadOnlyList<StartupFact> facts, Exception? inner)
        : base(message, inner)
    {
        Step = step;
        Facts = facts;
    }

    public StartupStep Step { get; }

    /// <summary>What the failure file lists under the message, in this order.</summary>
    public IReadOnlyList<StartupFact> Facts { get; }

    /// <summary>The folder step: the folder, its owner and the account the service runs as, and that <c>dbdatakit setup single-server</c> sets it up.</summary>
    /// <param name="folder">The folder that is not as the install set it.</param>
    /// <param name="owner">Its owner, or null to read it from the folder.</param>
    /// <param name="account">The account the service runs as, or null for the account of this process.</param>
    /// <param name="problem">What is wrong with it, as a sentence: "The folder is owned by another account."</param>
    /// <param name="inner">The exception that showed it, if any.</param>
    public static StartupStepException Folder(string folder, string? owner, string? account, string problem, Exception? inner = null)
    {
        owner ??= FileOwnership.DescribeOwner(folder) ?? "(could not be read)";
        account ??= StartupAccount.Current();
        return new(StartupStep.Folder,
            $"The folder '{folder}' is owned by {owner}, and the service runs as {account}. {problem} `dbdatakit setup single-server` sets the folder up.",
            [new("Folder", folder), new("Owner", owner)], inner);
    }

    /// <summary>
    /// The folder step's check of the data folder failed (<see cref="Startup.FolderPermissions"/>): the folder, its owner, the account, what is wrong,
    /// and the switch that starts the server on the folder as it is.
    /// </summary>
    /// <param name="folder">The data folder.</param>
    /// <param name="problems">What the check found, one sentence each.</param>
    /// <param name="settingsFile">The server's settings file, where the setting goes.</param>
    public static StartupStepException FolderPermissions(string folder, IReadOnlyList<string> problems, string settingsFile)
    {
        var owner = FileOwnership.DescribeOwner(folder) ?? "(could not be read)";
        var account = StartupAccount.Current();
        var skip = $"{Startup.FolderPermissions.Flag} or {Startup.FolderPermissions.Setting}: true in {settingsFile}";
        return new(StartupStep.Folder,
            $"The folder '{folder}' is owned by {owner}, and the service runs as {account}. {string.Join(" ", problems)} "
            + "`dbdatakit setup single-server` sets the folder up. To start on the folder as it is (a test's folder, a checkout), give `server run` "
            + $"{Startup.FolderPermissions.Flag} or set {Startup.FolderPermissions.Setting}: true in {settingsFile}.",
            [new("Folder", folder), new("Owner", owner), new("Skip the check with", skip)], null);
    }

    /// <summary>The keys step: the key file and the grant on it that is not expected.</summary>
    public static StartupStepException Keys(string keyFile, string unexpectedGrant, Exception? inner = null) =>
        new(StartupStep.Keys,
            $"The key file '{keyFile}' grants access to {unexpectedGrant}, which is not expected. Remove that grant, or move the file away so the service makes a new one.",
            [new("Key file", keyFile), new("Unexpected grant", unexpectedGrant)], inner);

    /// <summary>The certificate step: the certificate's file and the account that could not open or make it.</summary>
    public static StartupStepException Certificate(string file, string? account, Exception? inner = null)
    {
        account ??= StartupAccount.Current();
        return new(StartupStep.Certificate,
            $"The certificate '{file}' cannot be opened or made by {account}.",
            [new("Certificate", file)], inner);
    }

    /// <summary>The state step: the engine, the database and the migration that failed (null when the database could not be opened).</summary>
    public static StartupStepException State(string engine, string database, string? migration, Exception? inner = null) =>
        new(StartupStep.State,
            migration is null
                ? $"The state database '{database}' on {engine} cannot be opened."
                : $"The migration '{migration}' of the state database '{database}' on {engine} failed.",
            migration is null
                ? [new("Engine", engine), new("Database", database)]
                : [new("Engine", engine), new("Database", database), new("Migration", migration)], inner);

    /// <summary>The ports step: the port, the program holding it when Windows says, and the excluded port ranges (null to read them from Windows).</summary>
    public static StartupStepException Ports(int port, string? holder, IReadOnlyList<PortRange>? excludedRanges, Exception? inner = null)
    {
        var ranges = excludedRanges ?? ExcludedPortRanges.Read();
        var inRange = ranges.FirstOrDefault(r => r.Contains(port));
        var message = $"Port {port} cannot be bound"
                      + (holder is null ? "" : $": {holder} holds it")
                      + (inRange is null ? "" : $": it is inside the excluded port range {inRange}")
                      + ".";
        var facts = new List<StartupFact> { new("Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        if (holder is not null) facts.Add(new("Held by", holder));
        facts.Add(new("Excluded port ranges", ExcludedPortRanges.Describe(ranges)));
        return new(StartupStep.Ports, message, facts, inner);
    }

    /// <summary>
    /// The ports step of a server that was to choose a port of its sub-block and found none it could bind: the sub-block, and the excluded port ranges,
    /// named as covering it when they do.
    /// </summary>
    public static StartupStepException NoFreePort(PortRange block, IReadOnlyList<PortRange> excludedRanges, Exception? inner = null)
    {
        var overlapping = excludedRanges.Where(r => r.Start <= block.End && r.End >= block.Start).ToList();
        var covered = Enumerable.Range(block.Start, block.End - block.Start + 1).All(p => overlapping.Any(r => r.Contains(p)));
        var message = covered
            ? $"No port of {block} can be bound: the excluded port {(overlapping.Count == 1 ? "range" : "ranges")} {string.Join(", ", overlapping)} cover{(overlapping.Count == 1 ? "s" : "")} them all."
            : $"No port of {block} can be bound: each is held by another program or inside an excluded port range.";
        message += " Free one of them, or give the server a port of its own with --listen.";
        return new(StartupStep.Ports, message,
            [new("Ports", block.ToString()), new("Excluded port ranges", ExcludedPortRanges.Describe(excludedRanges))], inner);
    }

    /// <summary>The join step: the console's address and what it answered (null when it did not answer).</summary>
    public static StartupStepException Join(string consoleAddress, string? answer, Exception? inner = null) =>
        new(StartupStep.Join,
            answer is null
                ? $"The console at {consoleAddress} did not answer."
                : $"The console at {consoleAddress} answered: {answer}",
            [new("Console", consoleAddress), new("Answer", answer ?? "(none)")], inner);

    /// <summary>The join step when the console gave no answer: <paramref name="reason"/> says why (it could not be reached, or another certificate answered at its address).</summary>
    public static StartupStepException JoinNotMade(string consoleAddress, string reason, Exception? inner = null) =>
        new(StartupStep.Join, reason, [new("Console", consoleAddress), new("Answer", "(none)")], inner);
}
