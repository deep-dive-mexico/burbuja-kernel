namespace Burbuja.Core.Abstractions.Channel;

/// <summary>How much attention a notice deserves. The channel decides how to render each.</summary>
public enum NoticeKind
{
    Info,
    Warn,
    Alert,
}

/// <summary>
/// Something the host decided a human should know, phrased for a human. It carries no structure on
/// purpose: a notice is a sentence, and anything that needs to be acted on belongs in an approval or
/// a resource, not here.
///
/// <para>There is deliberately no deduplication field. Deciding when a condition is "new" requires
/// knowing the condition, and the only component that knows it is the one evaluating it — so
/// edge-triggering lives there, and this stays a message. A notifier that suppressed repeats on its
/// own would be guessing.</para>
/// </summary>
public sealed record Notice(
    NoticeKind Kind,
    string Text,
    string? Href = null,
    DateTimeOffset At = default);

/// <summary>
/// L3: the host's outbound notice path. Nothing in the kernel knows what a Discord is; a notice goes
/// here and the registered <see cref="IChannel"/>s decide what to do with it.
/// </summary>
public interface INotifier
{
    ValueTask NotifyAsync(Notice notice, CancellationToken ct);
}
