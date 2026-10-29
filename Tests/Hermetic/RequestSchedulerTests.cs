using QsrPriceBenchmarks.Core.Services;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// The turns chains take on a shared website (plan WP-C2): never more requests at once than the host's limit,
/// and no scrape starved by another that keeps asking.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RequestSchedulerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact(DisplayName = "Two scrapes sharing one slot take turns, one request each")]
    public async Task TwoScrapes_OnOneSlot_Alternate()
    {
        RequestScheduler scheduler = new(new Dictionary<string, int> { ["tg"] = 1 });
        List<string> order = [];
        Lock orderLock = new();

        async Task Scrape(string chain, IDisposable first)
        {
            IDisposable turn = first;
            for (int page = 0; ; page++)
            {
                lock (orderLock)
                {
                    order.Add(chain);
                }
                await Task.Delay(5);
                turn.Dispose();
                if (page == 9)
                {
                    return;
                }
                // Asks again straight away, as a scraper does for its next page.
                turn = await scheduler.For(chain).TakeTurnAsync("tg");
            }
        }

        IDisposable aFirst = await scheduler.For("A").TakeTurnAsync("tg");
        Task<IDisposable> bWaiting = scheduler.For("B").TakeTurnAsync("tg"); // B asks while A holds the slot
        Task a = Scrape("A", aFirst);
        Task b = Task.Run(async () => await Scrape("B", await bWaiting));
        await Task.WhenAll(a, b).WaitAsync(Bound);

        Assert.Equal(20, order.Count);
        for (int i = 1; i < order.Count; i++)
        {
            Assert.True(order[i] != order[i - 1],
                $"request {i} went to {order[i]} twice running: {string.Join("", order)}");
        }
    }

    [Fact(DisplayName = "No more requests run at once than the host's limit")]
    public async Task Limit_IsNeverExceeded()
    {
        RequestScheduler scheduler = new(new Dictionary<string, int> { ["google"] = 2 });
        int running = 0, peak = 0;

        async Task Search(string chain)
        {
            for (int i = 0; i < 5; i++)
            {
                using IDisposable turn = await scheduler.For(chain).TakeTurnAsync("google");
                int now = Interlocked.Increment(ref running);
                InterlockedMax(ref peak, now);
                await Task.Delay(3);
                Interlocked.Decrement(ref running);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(c => Task.Run(() => Search($"chain{c}")))).WaitAsync(Bound);

        Assert.Equal(2, peak);
    }

    [Fact(DisplayName = "A cancelled wait gives up its place and never takes the slot")]
    public async Task CancelledWait_GivesUpItsPlace()
    {
        RequestScheduler scheduler = new(new Dictionary<string, int> { ["tg"] = 1 });
        IDisposable held = await scheduler.For("A").TakeTurnAsync("tg");
        using CancellationTokenSource cancel = new();
        Task<IDisposable> cancelled = scheduler.For("B").TakeTurnAsync("tg", cancel.Token);
        Task<IDisposable> next = scheduler.For("C").TakeTurnAsync("tg");

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled).WaitAsync(Bound);
        held.Dispose();

        using IDisposable cTurn = await next.WaitAsync(Bound);
    }

    [Fact(DisplayName = "A host with no limit is never queued")]
    public async Task UnlimitedHost_IsNotQueued()
    {
        RequestScheduler scheduler = RequestScheduler.CreateDefault();
        using IDisposable first = await scheduler.For("A").TakeTurnAsync("www.burgerking.com.tr");
        Task<IDisposable> second = scheduler.For("B").TakeTurnAsync("www.burgerking.com.tr");

        Assert.True(second.IsCompletedSuccessfully);
        (await second).Dispose();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}
