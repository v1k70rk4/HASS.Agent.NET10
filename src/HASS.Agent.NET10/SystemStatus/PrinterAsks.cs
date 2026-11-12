using System.Collections.Concurrent;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>What one printer answered: its row, and the first jobs of its queue.</summary>
internal sealed record PrinterAnswer(PrinterRow Row, IReadOnlyList<PrintJobRow> Jobs);

/// <summary>
/// The printers asked one by one, all at the same time and against one deadline: a printer
/// whose print server does not answer is reported as one whose state is not known, and the
/// others are reported as they are. The question to a printer cannot be called off, so it is
/// left to finish on its own, and until it has, that printer is not asked again: one that
/// hangs holds one thread, not one more with every cycle.
///
/// Each question has a thread of its own, and none of them one of the pool's: the pool is
/// what the connection to Home Assistant and the commands run on, and a row of printers that
/// do not answer would take its threads one after the other, with everything else waiting
/// behind them, the end of the deadline included. Which printers there are is the user's to
/// say (the ones connected from a print server are the user's own), so there is a most to
/// how many questions are out at the same time, the ones left over from earlier reads
/// counted in: a printer past that is reported as one whose state is not known, and asked
/// when there is room again.
/// </summary>
internal sealed class PrinterAsks(TimeSpan deadline, int mostAtOnce = PrinterAsks.MostAtOnce)
{
    /// <summary>How many questions may be out at the same time, each on a thread of its own.</summary>
    internal const int MostAtOnce = 64;

    private readonly ConcurrentDictionary<string, Task<PrinterAnswer>> _stillAsking = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Asks every printer. <paramref name="prepare"/> runs here, on the caller's thread, once
    /// for each printer that is to be asked, and gives the question that runs on a thread of
    /// its own: what has to be taken from the caller (a copy of a user's token) is taken while
    /// the caller still has it.
    /// </summary>
    public (List<PrinterRow> Printers, List<PrintJobRow> Jobs) Collect(
        IReadOnlyList<string> names, Func<string, Func<PrinterAnswer>> prepare, int maximumJobs)
    {
        // One that answered after its deadline found something old by now; what it threw
        // was already reported as a printer whose state is not known. Every one of them is
        // looked at, also a printer's that is not among the names any more: it held a
        // thread all the same, and the room it leaves is counted below.
        foreach (var earlier in _stillAsking)
        {
            if (earlier.Value.IsCompleted)
            {
                _ = earlier.Value.Exception;
                _stillAsking.TryRemove(earlier);
            }
        }

        var underWay = _stillAsking.Count;
        var asked = new List<(string Name, Task<PrinterAnswer>? Task)>();
        foreach (var name in names)
        {
            if (_stillAsking.ContainsKey(name) || underWay >= mostAtOnce)
            {
                asked.Add((name, null));
                continue;
            }

            Task<PrinterAnswer> task;
            try
            {
                task = OnAThreadOfItsOwn(prepare(name));
                underWay++;
            }
            catch (Exception ex)
            {
                task = Task.FromException<PrinterAnswer>(ex);
            }

            asked.Add((name, task));
        }

        var running = asked.Where(item => item.Task is not null).Select(item => (Task)item.Task!).ToArray();
        if (running.Length > 0)
        {
            try
            {
                // Waited for here, on this thread, by the clock: nothing of the wait needs a
                // thread of the pool to come to its end.
                Task.WaitAll(running, deadline);
            }
            catch (AggregateException)
            {
                // What a question threw is its printer's own, below.
            }
        }

        var printers = new List<PrinterRow>();
        var jobs = new List<PrintJobRow>();
        foreach (var (name, task) in asked)
        {
            if (task is { IsCompletedSuccessfully: true })
            {
                printers.Add(task.Result.Row);
                jobs.AddRange(task.Result.Jobs.Take(Math.Max(0, maximumJobs - jobs.Count)));
                continue;
            }

            if (task is { IsCompleted: false })
            {
                _stillAsking[name] = task;
            }
            else
            {
                _ = task?.Exception;
            }

            printers.Add(Unknown(name));
        }

        return (printers, jobs);
    }

    // A thread made for the one question, which goes away with it. Not Task.Run: that is a
    // thread of the pool, which a question that hangs would keep from everything else.
    private static Task<PrinterAnswer> OnAThreadOfItsOwn(Func<PrinterAnswer> question) =>
        Task.Factory.StartNew(question, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>A printer whose state the spooler could not say.</summary>
    internal static PrinterRow Unknown(string name) => new(name, WorkOffline: false, PrintSpooler.StatusUnknown, Jobs: 0);
}
