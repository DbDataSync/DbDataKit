using DbDataKit.Connections.FlightSql;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.ArrowFlightSql;

/// <summary>
/// Reads a Flight SQL connection's settings out of Sync's <see cref="ConnectionConfig"/> and the already-resolved credential (the client, <see cref="FlightSqlSettings"/>, is the
/// shared <c>DbDataKit.Connections.FlightSql</c>). The driver-specific settings live in <see cref="ConnectionConfig.Properties"/> (the persisted shape has no other place for them)
/// under the keys <see cref="FlightSqlSettings"/> names.
/// </summary>
public static class FlightSqlSettingsReader
{
    extension(FlightSqlSettings)
    {
        public static FlightSqlSettings From(ConnectionConfig connection, string? credential)
        {
            var properties = connection.Properties;
            var useTls = !properties.TryGetValue(FlightSqlSettings.UseTlsKey, out var tls) || !FlightSqlSettings.IsFalse(tls);

            var auth = connection.AuthMode switch
            {
                AuthMode.SqlAuth => properties.TryGetValue(FlightSqlSettings.AuthMethodKey, out var method)
                    && Enum.TryParse<FlightSqlAuthMethod>(method, ignoreCase: true, out var parsed)
                    && parsed != FlightSqlAuthMethod.None
                        ? parsed
                        : FlightSqlAuthMethod.Basic,
                AuthMode.IntegratedAuth => throw new NotSupportedException(
                    "Arrow Flight SQL has no integrated authentication. Choose SQL Auth (basic or bearer) or None."),
                _ => FlightSqlAuthMethod.None,
            };

            if (auth == FlightSqlAuthMethod.Basic && string.IsNullOrEmpty(connection.UserId))
                throw new InvalidOperationException("Basic authentication needs a user id.");
            if (auth != FlightSqlAuthMethod.None && string.IsNullOrEmpty(credential))
                throw new InvalidOperationException(
                    auth == FlightSqlAuthMethod.Bearer
                        ? "Bearer authentication needs a token, and none is stored for this connection."
                        : "Basic authentication needs a password, and none is stored for this connection.");

            var mode = properties.TryGetValue(FlightSqlSettings.ParameterModeKey, out var pm)
                && Enum.TryParse<FlightSqlParameterMode>(pm, ignoreCase: true, out var parsedMode)
                    ? parsedMode
                    : FlightSqlParameterMode.Prepared;

            return new FlightSqlSettings(
                FlightSqlSettings.ResolveAddress(connection.ConnectionString, connection.Host, connection.Port, useTls),
                auth,
                auth == FlightSqlAuthMethod.Basic ? connection.UserId : null,
                auth == FlightSqlAuthMethod.None ? null : credential,
                properties.TryGetValue(FlightSqlSettings.SkipCertificateValidationKey, out var skip) && FlightSqlSettings.IsTrue(skip),
                string.IsNullOrWhiteSpace(connection.Database) ? null : connection.Database,
                FlightSqlSettings.Seconds(ConnectionTimeouts.ResolveConnectSeconds(connection)),
                FlightSqlSettings.Seconds(ConnectionTimeouts.ResolveCommandSeconds(connection)),
                mode,
                FlightSqlSettings.Text(properties, FlightSqlSettings.DataSourceKey),
                FlightSqlSettings.Text(properties, FlightSqlSettings.CompressionKey));
        }
    }
}
