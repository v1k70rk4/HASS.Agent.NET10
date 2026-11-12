namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// A read that may never come back (a provider of Windows that waits for a device that does not
/// answer), held to a deadline: the sensor cycle that asks for it goes on without it after
/// that. The read itself cannot be called off, so it is left to finish on its own thread, and
/// until it has, no other is started: one that hangs holds one thread, not one more with
/// every cycle. Two that ask at the same time (the sensor cycle and a Test button) share one
/// read.
/// </summary>
internal sealed class BoundedRead<T>(TimeSpan deadline)
{
    private readonly object _gate = new();
    private Task<T>? _running;
    private bool _givenUpOn;

    /// <summary>
    /// The value the read gives, or what it throws. A <see cref="TimeoutException"/> when it
    /// has not answered by the deadline, and at once when one that was already given up on
    /// still has not.
    /// </summary>
    public T Run(Func<T> read)
    {
        Task<T> task;
        lock (_gate)
        {
            if (_running is { IsCompleted: false } earlier)
            {
                if (_givenUpOn)
                {
                    throw new TimeoutException("An earlier read has still not answered.");
                }

                // Asked for by someone else a moment ago: its answer is as good for this one.
                task = earlier;
            }
            else
            {
                // One that answered after its deadline found something that is old by now, and
                // what it threw was already reported as a timeout.
                _ = _running?.Exception;
                _givenUpOn = false;
                task = _running = Task.Run(read);
            }
        }

        if (!((IAsyncResult)task).AsyncWaitHandle.WaitOne(deadline))
        {
            lock (_gate)
            {
                if (ReferenceEquals(_running, task))
                {
                    _givenUpOn = true;
                }
            }

            throw new TimeoutException($"No answer in {deadline.TotalSeconds:0.#} s.");
        }

        lock (_gate)
        {
            if (ReferenceEquals(_running, task))
            {
                _running = null;
                _givenUpOn = false;
            }
        }

        return task.GetAwaiter().GetResult();
    }
}
