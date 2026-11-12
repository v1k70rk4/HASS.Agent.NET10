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
/// </summary>
internal sealed class PrinterAsks(TimeSpan deadline)
{
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
        var asked = new List<(string Name, Task<PrinterAnswer>? Task)>();
        foreach (var name in names)
        {
            if (_stillAsking.TryGetValue(name, out var earlier) && !earlier.IsCompleted)
            {
                asked.Add((name, null));
                continue;
            }

            // One that answered after its deadline found something old by now; what it threw
            // was already reported as a printer whose state is not known.
            if (earlier is not null)
            {
                _ = earlier.Exception;
                _stillAsking.TryRemove(new KeyValuePair<string, Task<PrinterAnswer>>(name, earlier));
            }

            Task<PrinterAnswer> task;
            try
            {
                task = Task.Run(prepare(name));
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
            Task.WaitAny(Task.WhenAll(running).ContinueWith(_ => { }, TaskScheduler.Default), Task.Delay(deadline));
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

    /// <summary>A printer whose state the spooler could not say.</summary>
    internal static PrinterRow Unknown(string name) => new(name, WorkOffline: false, PrintSpooler.StatusUnknown, Jobs: 0);
}
