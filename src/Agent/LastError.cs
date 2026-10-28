namespace Agent;

/// <summary>
/// The heartbeat's last_error: one slot shared by the sampling loop, the
/// project walker and the uploader, holding whichever fault happened most
/// recently. Each loop catches per iteration, records here and carries on;
/// the design's "keep sampling, drain the backlog later" promise depends on
/// no single exception taking the host down. The uploader clears the slot
/// after a good upload only when the fault was its own, so a sampler fault
/// stays visible in the portal until something newer replaces it.
/// </summary>
public sealed class LastError
{
    private sealed record Entry(string Source, string Message);

    private volatile Entry? _current;

    public string? Message => _current?.Message;

    public void Record(string source, string reason) =>
        _current = new Entry(source, $"{DateTimeOffset.UtcNow:HH:mm:ss}Z {source} failed: {reason}");

    public void Clear(string source)
    {
        if (_current?.Source == source)
            _current = null;
    }
}
