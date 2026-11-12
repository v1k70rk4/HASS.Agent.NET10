using System.Diagnostics;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// A read held to a deadline: the sensor cycle goes on without one that does not answer, and
/// no second one is started while the first still hangs.
/// </summary>
public sealed class BoundedReadTests
{
    [Fact]
    public void A_read_that_answers_gives_its_value_or_what_it_threw()
    {
        var bounded = new BoundedRead<int>(TimeSpan.FromSeconds(5));

        Assert.Equal(42, bounded.Run(() => 42));
        var thrown = Assert.Throws<InvalidOperationException>(() => bounded.Run(() => throw new InvalidOperationException("spooler")));
        Assert.Equal("spooler", thrown.Message);

        // Neither holds up the next one.
        Assert.Equal(7, bounded.Run(() => 7));
    }

    [Fact]
    public async Task Two_that_ask_at_the_same_time_share_one_read()
    {
        var bounded = new BoundedRead<int>(TimeSpan.FromSeconds(10));
        using var release = new ManualResetEventSlim();
        using var underWay = new ManualResetEventSlim();
        var started = 0;
        int Slow()
        {
            Interlocked.Increment(ref started);
            underWay.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return 5;
        }

        // The sensor cycle asks, and while Windows is still answering, a Test button asks too.
        var cycle = Task.Run(() => bounded.Run(Slow), TestContext.Current.CancellationToken);
        Assert.True(underWay.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var button = Task.Run(() => bounded.Run(Slow), TestContext.Current.CancellationToken);
        var first = await Task.WhenAny(button, Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        Assert.NotSame(button, first);

        release.Set();

        Assert.Equal(5, await cycle);
        Assert.Equal(5, await button);
        Assert.Equal(1, Volatile.Read(ref started));
    }

    [Fact]
    public void A_read_that_hangs_is_left_at_the_deadline_and_not_started_again_while_it_hangs()
    {
        var bounded = new BoundedRead<int>(TimeSpan.FromMilliseconds(150));
        using var release = new ManualResetEventSlim();
        var started = 0;
        int Hangs()
        {
            Interlocked.Increment(ref started);
            release.Wait(TimeSpan.FromSeconds(30));
            return 1;
        }

        var watch = Stopwatch.StartNew();
        Assert.Throws<TimeoutException>(() => bounded.Run(Hangs));
        Assert.InRange(watch.ElapsedMilliseconds, 100, 5000);

        // The next cycles do not wait for it and do not start another.
        watch.Restart();
        Assert.Throws<TimeoutException>(() => bounded.Run(Hangs));
        Assert.Throws<TimeoutException>(() => bounded.Run(Hangs));
        Assert.InRange(watch.ElapsedMilliseconds, 0, 100);
        Assert.Equal(1, Volatile.Read(ref started));

        // Once it has come back, a new read is made; what the late one found is not used.
        release.Set();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            try
            {
                return bounded.Run(() => 2) == 2;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Volatile.Read(ref started));
    }
}
