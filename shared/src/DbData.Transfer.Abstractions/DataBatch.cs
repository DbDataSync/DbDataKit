using System.Data.Common;
using Apache.Arrow;

namespace DbData.Transfer;

/// <summary>
/// A bounded set of rows with a schema. Interchange is reader-shaped first (<see cref="AsDataReader"/>) with Arrow as an adapter
/// (<see cref="AsArrow"/>); each call returns a fresh forward-only view.
/// </summary>
public abstract class DataBatch
{
    public abstract int Rows { get; }
    public abstract TransferSchema Schema { get; }

    /// <summary>A forward-only reader over the rows. Values are CLR values of the canonical contract (see ValueConverter). Never buffers into a DataTable.</summary>
    public abstract DbDataReader AsDataReader();

    /// <summary>The rows as an Arrow record batch (types from the canonical Arrow table).</summary>
    public abstract RecordBatch AsArrow();
}
