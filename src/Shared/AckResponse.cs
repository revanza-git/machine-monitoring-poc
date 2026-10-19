namespace Shared;

/// <summary>
/// Reply to <c>POST /v1/samples</c>. The agent deletes outbox rows with
/// seq &lt;= <see cref="AckThrough"/>. <see cref="Config"/> is null unless the
/// agent's config version is behind.
/// </summary>
public sealed record AckResponse(long AckThrough, AgentConfig? Config);
