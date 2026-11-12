using System.Diagnostics;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The printers asked one by one against one deadline: a printer whose print server does not
/// answer is reported as one whose state is not known, and does not hide the others.
/// </summary>
public sealed class PrinterAsksTests
{
    private static Func<PrinterAnswer> Answers(string name, int jobs = 0) => () => new PrinterAnswer(
        new PrinterRow(name, WorkOffline: false, State: 0, Jobs: jobs),
        Enumerable.Range(1, jobs).Select(number => new PrintJobRow(name, $"{name} {number}", "viktor", 0, 1)).ToList());

    [Fact]
    public void Every_printer_that_answers_is_reported_with_its_jobs_in_the_order_of_the_names()
    {
        var asks = new PrinterAsks(TimeSpan.FromSeconds(10));

        var (printers, jobs) = asks.Collect(["A", "B", "C"], name => Answers(name, name == "B" ? 3 : 1), maximumJobs: 4);

        Assert.Equal(["A", "B", "C"], printers.Select(printer => printer.Name));
        Assert.Equal([1, 3, 1], printers.Select(printer => printer.Jobs));
        // The list is cut at the most there may be; the counts are not.
        Assert.Equal(["A 1", "B 1", "B 2", "B 3"], jobs.Select(job => job.Document));
    }

    [Fact]
    public void A_printer_that_cannot_be_asked_is_one_whose_state_is_not_known()
    {
        var asks = new PrinterAsks(TimeSpan.FromSeconds(10));

        var (printers, _) = asks.Collect(
            ["A", "B", "C"],
            name => name switch
            {
                "A" => throw new InvalidOperationException("no copy of the token"),
                "B" => () => throw new System.ComponentModel.Win32Exception(1801),
                _ => Answers(name)
            },
            maximumJobs: 20);

        Assert.Equal(["A", "B", "C"], printers.Select(printer => printer.Name));
        Assert.Equal([PrintSpooler.StatusUnknown, PrintSpooler.StatusUnknown, 0L], printers.Select(printer => printer.State));
    }

    [Fact]
    public void A_printer_that_hangs_does_not_hide_the_others_and_is_not_asked_again_while_it_hangs()
    {
        var asks = new PrinterAsks(TimeSpan.FromSeconds(1));
        using var release = new ManualResetEventSlim();
        var asked = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        Func<PrinterAnswer> Prepare(string name) => () =>
        {
            asked.AddOrUpdate(name, 1, (_, count) => count + 1);
            if (name == @"\\away\Office")
            {
                release.Wait(TimeSpan.FromSeconds(60));
            }

            return Answers(name, 1)();
        };
        string[] names = ["Brother", @"\\away\Office", "Kitchen"];

        // The ones that answer are reported as they are, the one that hangs as not known, and
        // the read takes the deadline, not the hang.
        var watch = Stopwatch.StartNew();
        var (printers, jobs) = asks.Collect(names, Prepare, maximumJobs: 20);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 30_000);
        Assert.Equal(names, printers.Select(printer => printer.Name));
        Assert.Equal([0L, PrintSpooler.StatusUnknown, 0L], printers.Select(printer => printer.State));
        Assert.Equal(["Brother 1", "Kitchen 1"], jobs.Select(job => job.Document));

        // The next read does not wait for it, and does not ask it again.
        watch.Restart();
        (printers, _) = asks.Collect(names, Prepare, maximumJobs: 20);
        Assert.InRange(watch.ElapsedMilliseconds, 0, 900);
        Assert.Equal(PrintSpooler.StatusUnknown, printers[1].State);
        Assert.Equal(1, asked[@"\\away\Office"]);
        Assert.Equal(2, asked["Brother"]);

        // Once it has answered, it is asked again, and its answer is reported.
        release.Set();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var (again, _) = asks.Collect(names, Prepare, maximumJobs: 20);
            return again[1].State == 0;
        }, TimeSpan.FromSeconds(30)));
        Assert.Equal(2, asked[@"\\away\Office"]);
    }
}
