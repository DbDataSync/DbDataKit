namespace DbData.Odbc.Metadata;

/// <summary>
/// The ODBC driver reported a failure from a catalog function or from SQLGetInfo. Only the function name and the SQLSTATE
/// are kept: the driver's message can quote a name or a path, so it is never repeated.
/// </summary>
public sealed class OdbcCatalogException(string function, string sqlState)
    : Exception($"The ODBC driver's {function} failed (SQLSTATE {sqlState}).")
{
    public string Function { get; } = function;
    public string SqlState { get; } = sqlState;

    /// <summary>The driver does not implement this function, or this part of it (IM001, HYC00, HY106 and friends).</summary>
    public bool NotSupported => SqlState is "IM001" or "HYC00" or "HY106" or "HYC01" or "S1C00" or "IM017" or "IM002";
}
