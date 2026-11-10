using System.Threading.Channels;

namespace SonNuage.Server.Jobs;

/// <summary>
/// Signal « les tâches ont changé » (création, démarrage, fin, annulation, pause, avancement, nouvelles lignes de
/// journal) pour les abonnés en direct de cette instance : le volet Tâches en push. Le signal ne dit pas quoi : chaque
/// abonné relit ce qu'il affiche. Les changements des autres instances arrivent par la relève périodique des abonnés.
/// </summary>
public sealed class JobChanges
{
    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscribers = [];

    public int Subscribers
    {
        get { lock (_lock) return _subscribers.Count; }
    }

    public void Notify()
    {
        lock (_lock)
            foreach (var s in _subscribers) s.Signal();
    }

    public Subscription Subscribe()
    {
        var s = new Subscription(this);
        lock (_lock) _subscribers.Add(s);
        return s;
    }

    /// <summary>Abonnement : au plus un signal en attente, les changements rapprochés se résument en un seul.</summary>
    public sealed class Subscription(JobChanges owner) : IDisposable
    {
        private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

        public ChannelReader<bool> Reader => _signal.Reader;

        internal void Signal() => _signal.Writer.TryWrite(true);

        public void Dispose()
        {
            lock (owner._lock) owner._subscribers.Remove(this);
            _signal.Writer.TryComplete();
        }
    }
}
