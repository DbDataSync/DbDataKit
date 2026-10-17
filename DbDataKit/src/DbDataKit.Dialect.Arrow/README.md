# DbDataKit.Dialect.Arrow

Canonical type to Apache Arrow mapping (Apache.Arrow 23.0.0), ported from Proxy's `ArrowColumnWriter`.

## Public contract
- `ArrowTypeMap.ToArrow(CanonicalType) : TypeMapping<IArrowType>` and `FromArrow(IArrowType) : TypeMapping<CanonicalType>`; a rule table keyed by canonical kind, not an if-chain.
- `ClrCanonicalMap.FromClr(Type, providerTypeName, precision, scale)` and `.ToArrow(...)`: the CLR-type decisions Proxy makes (a `DateTime` column whose provider type is `date` is a date).

## Decisions
- Timestamp is microseconds; zone-aware timestamps carry `"UTC"`; scale above 6 is Lossy.
- Time is `Time64(us)`; date is `Date32`; interval is `Duration(us)`.
- Decimal: `Decimal128` to 38 digits, `Decimal256` (Widened) to 76, above that Unmappable; no precision reported falls back to (38,9) as Lossy; invalid scale is Lossy.
- Unsigned integers keep unsigned Arrow types. Guid, json and xml travel as text (Widened). Length bounds on string/binary are not carried (Widened).
- List is Unmappable to Arrow (opaque); an Arrow list reads back as canonical `list` (Lossy).
- Arrow types have no value equality: compare `Name`/properties, not `Equals`.
