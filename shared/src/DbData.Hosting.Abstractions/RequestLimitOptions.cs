namespace DbData.Hosting;

/// <summary>Safe defaults for request limits (Proxy S-6). Products override per need.</summary>
public sealed class RequestLimitOptions
{
    /// <summary>Concurrent in-flight requests before new ones get 503.</summary>
    public int MaxConcurrentRequests { get; set; } = 100;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);

    public TimeSpan RequestHeadersTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum request body.</summary>
    public long MaxRequestBodyBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Ceiling for gRPC/SignalR messages.</summary>
    public int MaxMessageBytes { get; set; } = 4 * 1024 * 1024;

    public void Validate()
    {
        if (MaxConcurrentRequests < 1) throw new ArgumentOutOfRangeException(nameof(MaxConcurrentRequests));
        if (RequestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (RequestHeadersTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RequestHeadersTimeout));
        if (KeepAliveTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(KeepAliveTimeout));
        if (MaxRequestBodyBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxRequestBodyBytes));
        if (MaxMessageBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
    }
}
