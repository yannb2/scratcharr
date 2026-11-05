namespace Scratcharr.Jobs;

/// <summary>
/// Works out how long the running job has left from how fast its percent has moved lately.
/// It uses the last <see cref="Window"/> of progress so a slower or faster stretch (cache, a busy pool,
/// the read-back after a copy) shows up within a couple of minutes, and the first sample as its start,
/// so the files a resumed copy skips at once don't make it look faster than it is.
/// </summary>
public sealed class Eta
{
    /// <summary>How far back the speed is measured.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>No guess before the job has run this long since its first sample.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    private readonly Queue<(DateTime At, int Percent)> _samples = new();
    private readonly Lock _lock = new();

    /// <summary>Forgets the previous job.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _samples.Clear();
        }
    }

    /// <summary>Notes the percent done at <paramref name="nowUtc"/>.</summary>
    public void Add(int percent, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_samples.Count > 0 && percent < _samples.Last().Percent)
            {
                _samples.Clear();
            }

            _samples.Enqueue((nowUtc, percent));
            // Keep one sample older than the window so the speed always spans the whole window.
            while (_samples.Count > 2 && nowUtc - _samples.ElementAt(1).At > Window)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>Percent per second over the window, or null while there is too little to go on.</summary>
    public double? Rate(DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            var (firstAt, firstPercent) = _samples.Peek();
            var (lastAt, lastPercent) = _samples.Last();
            var seconds = (lastAt - firstAt).TotalSeconds;
            if (lastPercent <= firstPercent || nowUtc - firstAt < Settle || seconds <= 0)
            {
                return null;
            }

            return (lastPercent - firstPercent) / seconds;
        }
    }

    /// <summary>Time left, counting down from the last sample; null while there is too little to go on.</summary>
    public TimeSpan? Left(DateTime nowUtc)
    {
        if (Rate(nowUtc) is not { } rate)
        {
            return null;
        }

        int percent;
        DateTime at;
        lock (_lock)
        {
            (at, percent) = _samples.Last();
        }

        var seconds = (100 - percent) / rate - (nowUtc - at).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Max(0, seconds));
    }

    /// <summary>Bytes per second for a job of <paramref name="size"/> bytes, or null.</summary>
    public long? Speed(long size, DateTime nowUtc) =>
        size > 0 && Rate(nowUtc) is { } rate ? (long)(rate * size / 100) : null;

    /// <summary>"about 4 min left", "about 1 h 20 min left", "less than a minute left".</summary>
    public static string Text(TimeSpan left)
    {
        var minutes = (int)Math.Ceiling(left.TotalMinutes);
        return minutes switch
        {
            <= 1 => "less than a minute left",
            < 60 => $"about {minutes} min left",
            _ when minutes % 60 == 0 => $"about {minutes / 60} h left",
            _ => $"about {minutes / 60} h {minutes % 60} min left"
        };
    }

    /// <summary>"210 MB/s".</summary>
    public static string SpeedText(long bytesPerSecond) => Format.Size(bytesPerSecond) + "/s";
}
