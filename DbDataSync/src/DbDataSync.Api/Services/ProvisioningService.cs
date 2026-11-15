using DbDataKit.Web.Auth;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Api.Services;

/// <summary>
/// The Sync API's database setup: <see cref="MappingProvisioner"/> (the library <c>DbDataSync.Provisioning</c>, plan 28c E21) with the signed-in person as the author of a mapping it saves.
/// </summary>
public sealed class ProvisioningService(
    ConfigRepository configRepository, IConnectionFactory connections, CurrentUser currentUser,
    MappingColumnReader columnReader, DriverRegistry driverRegistry)
    : MappingProvisioner(configRepository, connections, () => currentUser.Author, columnReader, driverRegistry);
