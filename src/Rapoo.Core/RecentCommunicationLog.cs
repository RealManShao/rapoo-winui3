namespace Rapoo.Core;

/// <summary>Bounded, deduplicated diagnostics; formats text only when requested by the UI.</summary>
public sealed class RecentCommunicationLog(int capacity = 128)
{
    private readonly Lock sync = new();
    private readonly LinkedList<Entry> entries = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> index = new(StringComparer.Ordinal);
    private const int MaxMessageLength = 2048;
    private sealed class Entry(string text, DateTimeOffset receivedAt)
    {
        public string Text { get; } = text;
        public DateTimeOffset ReceivedAt { get; set; } = receivedAt;
    }

    public void Add(string text, DateTimeOffset receivedAt)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (text.Length > MaxMessageLength) text = text[..MaxMessageLength];
        lock (sync)
        {
            if (index.TryGetValue(text, out var existing))
            {
                existing.Value.ReceivedAt = receivedAt;
                entries.Remove(existing);
                entries.AddLast(existing);
                return;
            }
            if (entries.Count == capacity)
            {
                index.Remove(entries.First!.Value.Text);
                entries.RemoveFirst();
            }
            index.Add(text, entries.AddLast(new Entry(text, receivedAt)));
        }
    }

    public string Format()
    {
        lock (sync)
            return string.Join(Environment.NewLine, entries.Select(entry =>
                $"[{entry.ReceivedAt.LocalDateTime:HH:mm:ss.fff}] {entry.Text}"));
    }

    public void Clear()
    {
        lock (sync) { entries.Clear(); index.Clear(); }
    }
}
