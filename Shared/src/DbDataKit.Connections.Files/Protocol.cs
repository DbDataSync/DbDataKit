using FluentFTP.Exceptions;
using Renci.SshNet.Common;

namespace DbDataKit.Connections.Files;

/// <summary>What the libraries of the protocols throw, as the shared failure kinds: a rejected login, a missing path, a refused permission, a network or timeout failure.</summary>
internal static class Protocol
{
    public static ConnectionFailure? Classify(Exception exception)
    {
        switch (exception)
        {
            case SshAuthenticationException: return new(ConnectionFailureKind.Login, "ssh-auth", FileFailures.Engine);
            case SftpPathNotFoundException: return new(ConnectionFailureKind.NotFound, "sftp-not-found", FileFailures.Engine);
            case SftpPermissionDeniedException: return new(ConnectionFailureKind.Permission, "sftp-permission", FileFailures.Engine);
            case SshOperationTimeoutException: return new(ConnectionFailureKind.Timeout, "ssh-timeout", FileFailures.Engine);
            case ScpException: return new(ConnectionFailureKind.NotFound, "scp", FileFailures.Engine);
            case SshConnectionException: return new(ConnectionFailureKind.Network, "ssh-connection", FileFailures.Engine);
            case SshException: return new(ConnectionFailureKind.Other, "ssh", FileFailures.Engine);
            case FtpAuthenticationException: return new(ConnectionFailureKind.Login, "ftp-auth", FileFailures.Engine);
            case FtpInvalidCertificateException: return new(ConnectionFailureKind.Tls, "ftp-certificate", FileFailures.Engine);
            case FtpMissingObjectException: return new(ConnectionFailureKind.NotFound, "ftp-not-found", FileFailures.Engine);
            case FtpCommandException c when c.CompletionCode is "550" or "450": return new(ConnectionFailureKind.NotFound, "ftp-" + c.CompletionCode, FileFailures.Engine);
            case FtpCommandException c when c.CompletionCode is "530" or "430": return new(ConnectionFailureKind.Login, "ftp-" + c.CompletionCode, FileFailures.Engine);
            case FtpCommandException c when c.CompletionCode is "553" or "532": return new(ConnectionFailureKind.Permission, "ftp-" + c.CompletionCode, FileFailures.Engine);
            case FtpException: return new(ConnectionFailureKind.Network, "ftp", FileFailures.Engine);
        }

        return null;
    }
}
