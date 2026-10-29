namespace QsrPriceBenchmarks.Core.Services;

/// <summary>
/// One per process: hands out turns to send requests to a website, at most a fixed number at once per host, and
/// takes the scrapes waiting for a host in turn (round-robin), so one chain's scrape never starves another's.
///
/// The limits are what ONE run already used before several could run at once — one Tıkla Gelsin page at a time,
/// up to four Google Maps searches — so a single chain runs exactly as fast as before, and chains running together
/// share that same total instead of multiplying the load on the site. A host without a limit is not queued
/// (a chain's own website: only that chain's run ever visits it).
///
/// <see cref="SemaphoreSlim"/> would not do: it promises no order among waiters, so a scrape that releases and
/// asks again at once could keep winning.
/// </summary>
public sealed class RequestScheduler(IReadOnlyDictionary<string, int> slotsPerHost)
{
    public const string TiklaGelsin = "www.tiklagelsin.com";
    public const string GoogleMaps = "www.google.com";

    private readonly Dictionary<string, HostQueue> _hosts = slotsPerHost.ToDictionary(
        h => h.Key, h => new HostQueue(h.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>The limits one run used before chains could run together (see the class summary).</summary>
    public static RequestScheduler CreateDefault()
    {
        return new RequestScheduler(new Dictionary<string, int> { [TiklaGelsin] = 1, [GoogleMaps] = 4 });
    }

    /// <summary>The gate one run passes its requests through; <paramref name="session"/> is what takes turns.</summary>
    public RequestGate For(string session)
    {
        return new RequestGate(this, session);
    }

    internal Task<IDisposable> TakeTurnAsync(string host, string session, CancellationToken ct)
    {
        return _hosts.TryGetValue(host, out HostQueue? queue)
            ? queue.TakeTurnAsync(session, ct)
            : Task.FromResult<IDisposable>(NoTurn.Instance);
    }

    /// <summary>One host's free slots and its waiters, grouped by session and served round-robin.</summary>
    private sealed class HostQueue(int slots)
    {
        private readonly Lock _lock = new();
        private readonly LinkedList<string> _rotation = new();
        private readonly Dictionary<string, Queue<TaskCompletionSource<IDisposable>>> _waiting = [];
        private int _free = slots;

        public Task<IDisposable> TakeTurnAsync(string session, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            TaskCompletionSource<IDisposable> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock)
            {
                if (_free > 0 && _rotation.Count == 0)
                {
                    _free--;
                    return Task.FromResult<IDisposable>(new Turn(this));
                }

                if (!_waiting.TryGetValue(session, out Queue<TaskCompletionSource<IDisposable>>? queue))
                {
                    queue = new Queue<TaskCompletionSource<IDisposable>>();
                    _waiting[session] = queue;
                }
                if (queue.Count == 0)
                {
                    _rotation.AddLast(session);
                }
                queue.Enqueue(waiter);
            }

            if (ct.CanBeCanceled)
            {
                CancellationTokenRegistration registration = ct.Register(() => Withdraw(session, waiter, ct));
                _ = waiter.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }
            return waiter.Task;
        }

        private void Release()
        {
            TaskCompletionSource<IDisposable>? next = null;
            lock (_lock)
            {
                while (next is null && _rotation.First is { } first)
                {
                    string session = first.Value;
                    _rotation.RemoveFirst();
                    Queue<TaskCompletionSource<IDisposable>> queue = _waiting[session];
                    next = queue.Dequeue();
                    if (queue.Count > 0)
                    {
                        _rotation.AddLast(session); // its next request waits behind every other session's
                    }
                }
                if (next is null)
                {
                    _free++;
                    return;
                }
            }
            next.TrySetResult(new Turn(this)); // the slot passes straight to the next session in turn
        }

        private void Withdraw(string session, TaskCompletionSource<IDisposable> waiter, CancellationToken ct)
        {
            lock (_lock)
            {
                if (!_waiting.TryGetValue(session, out Queue<TaskCompletionSource<IDisposable>>? queue)
                    || !queue.Contains(waiter))
                {
                    return; // already handed a turn
                }
                _waiting[session] = new Queue<TaskCompletionSource<IDisposable>>(queue.Where(w => w != waiter));
                if (_waiting[session].Count == 0)
                {
                    _rotation.Remove(session);
                }
            }
            waiter.TrySetCanceled(ct);
        }

        /// <summary>A held slot; disposing it hands the slot on.</summary>
        private sealed class Turn(HostQueue owner) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    owner.Release();
                }
            }
        }
    }

    /// <summary>The turn on a host nobody queues for: nothing to hand on.</summary>
    internal sealed class NoTurn : IDisposable
    {
        public static readonly NoTurn Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// The requests of one run, queued with every other run's by a shared <see cref="RequestScheduler"/>.
/// <see cref="Unlimited"/> queues nothing — a run on its own (the CLI, a test) behaves as before.
/// </summary>
public sealed class RequestGate
{
    private readonly RequestScheduler? _scheduler;
    private readonly string _session;

    internal RequestGate(RequestScheduler? scheduler, string session)
    {
        _scheduler = scheduler;
        _session = session;
    }

    public static RequestGate Unlimited { get; } = new(null, "");

    /// <summary>Waits for a turn on <paramref name="host"/>; dispose the result once the request is done.</summary>
    public Task<IDisposable> TakeTurnAsync(string host, CancellationToken ct = default)
    {
        return _scheduler is null
            ? Task.FromResult<IDisposable>(RequestScheduler.NoTurn.Instance)
            : _scheduler.TakeTurnAsync(host, _session, ct);
    }

}
