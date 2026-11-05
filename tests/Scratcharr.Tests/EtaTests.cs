using Scratcharr.Jobs;

namespace Scratcharr.Tests;

[TestClass]
public sealed class EtaTests
{
    private static readonly DateTime Start = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void Time_left_comes_from_how_fast_the_percent_moves()
    {
        var eta = new Eta();
        Assert.IsNull(eta.Left(Start));
        eta.Add(1, Start);
        eta.Add(2, Start.AddSeconds(2));
        Assert.IsNull(eta.Left(Start.AddSeconds(2)), "no guess in the first seconds");

        // 1% every 6 seconds: 90% to go = 9 minutes.
        for (var p = 2; p <= 10; p++)
        {
            eta.Add(p, Start.AddSeconds((p - 1) * 6));
        }

        var at = Start.AddSeconds(54);
        Assert.AreEqual(540, eta.Left(at)!.Value.TotalSeconds, 0.5);
        Assert.AreEqual("about 9 min left", Eta.Text(eta.Left(at)!.Value));
        // Counts down between updates.
        Assert.AreEqual(510, eta.Left(at.AddSeconds(30))!.Value.TotalSeconds, 0.5);
        // 50 GB job: 1% = 512 MB every 6 s.
        Assert.AreEqual(50L * 1024 * 1024 * 1024 / 600, eta.Speed(50L * 1024 * 1024 * 1024, at));
    }

    [TestMethod]
    public void A_change_of_speed_shows_within_the_window()
    {
        var eta = new Eta();
        var t = Start;
        for (var p = 1; p <= 20; p++)
        {
            eta.Add(p, t);
            t = t.AddSeconds(1);
        }

        // Now 10 times slower for longer than the window.
        for (var p = 21; p <= 40; p++)
        {
            t = t.AddSeconds(10);
            eta.Add(p, t);
        }

        Assert.AreEqual(600, eta.Left(t)!.Value.TotalSeconds, 15);
    }

    [TestMethod]
    public void Starting_from_a_resumed_point_is_not_counted_as_speed()
    {
        var eta = new Eta();
        // A resumed copy jumps straight to 60% (the files already there), then moves 1% every 10 s.
        eta.Add(60, Start);
        eta.Add(61, Start.AddSeconds(10));
        Assert.AreEqual(390, eta.Left(Start.AddSeconds(10))!.Value.TotalSeconds, 0.5);
    }

    [TestMethod]
    public void The_text_is_rounded_up_to_whole_minutes()
    {
        Assert.AreEqual("less than a minute left", Eta.Text(TimeSpan.FromSeconds(40)));
        Assert.AreEqual("about 2 min left", Eta.Text(TimeSpan.FromSeconds(61)));
        Assert.AreEqual("about 1 h left", Eta.Text(TimeSpan.FromMinutes(60)));
        Assert.AreEqual("about 1 h 20 min left", Eta.Text(TimeSpan.FromMinutes(79.5)));
        Assert.AreEqual("210 MB/s", Eta.SpeedText(210L * 1024 * 1024));
    }
}
