using Arrkit.Processes;
using Microsoft.Extensions.Options;
using Scratcharr.Options;

namespace Scratcharr.Tools;

public sealed record ToolStatus
{
    public required ToolDefinition Tool { get; init; }
    public string? Installed { get; init; }
    public bool Missing { get; init; }
    public string? Latest { get; init; }
    public string? UpstreamError { get; init; }

    public string? Pinned => Tool.Pinned;

    public bool UpdateAvailable => Latest != null && (Installed ?? Pinned) is { } current
                                   && VersionComparer.Instance.Compare(Latest, current) > 0;
}

/// <summary>
/// Asks every tool for its version when the app starts and looks for newer versions
/// upstream once a day. Nothing is installed: an update is a new image.
/// </summary>
public sealed class ToolStatusService(IOptions<ScratcharrOptions> options, ILogger<ToolStatusService> logger) : BackgroundService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly ScratcharrOptions _options = options.Value;
    private readonly SemaphoreSlim _checkNow = new(0);

    public IReadOnlyList<ToolStatus> Statuses { get; private set; } = [];
    public DateTime? UpstreamCheckedAt { get; private set; }
    public bool Checking { get; private set; }

    public int UpdateCount => Statuses.Count(s => s.UpdateAvailable);

    public event Action? Changed;

    public void CheckNow() => _checkNow.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var upstream = _options.Updates.Enabled;
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(upstream, stoppingToken);
            try
            {
                var interval = TimeSpan.FromHours(Math.Max(1, _options.Updates.IntervalHours));
                // The button always looks upstream; the daily check only when it is on.
                upstream = await _checkNow.WaitAsync(interval, stoppingToken) || _options.Updates.Enabled;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RefreshAsync(bool upstream, CancellationToken cancellationToken)
    {
        Checking = true;
        Changed?.Invoke();
        try
        {
            var statuses = new List<ToolStatus>();
            foreach (var tool in ToolCatalog.All)
            {
                statuses.Add(await CheckAsync(tool, upstream, cancellationToken));
            }

            Statuses = statuses;
            if (upstream)
            {
                UpstreamCheckedAt = DateTime.UtcNow;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Tool check failed");
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    private async Task<ToolStatus> CheckAsync(ToolDefinition tool, bool upstream, CancellationToken cancellationToken)
    {
        var result = await ToolRunner.RunAsync(tool.Command(_options.Tools), tool.VersionArguments, TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken);
        var installed = result.NotStarted ? null : ToolCatalog.InstalledVersion(tool, result.Output + "\n" + result.Error);

        string? latest = null, error = null;
        var previous = Statuses.FirstOrDefault(s => s.Tool.Id == tool.Id);
        if (upstream && tool.Upstream == UpstreamKind.GitHubRelease)
        {
            try
            {
                var json = await Http.GetStringAsync($"https://api.github.com/repos/{tool.UpstreamSources[0]}/releases?per_page=30", cancellationToken);
                latest = ToolCatalog.GitHubTag(json);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                error = e.Message;
            }
        }
        else if (previous != null)
        {
            (latest, error) = (previous.Latest, previous.UpstreamError);
        }

        return new ToolStatus { Tool = tool, Installed = installed, Missing = result.NotStarted, Latest = latest, UpstreamError = error };
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Scratcharr/{AppInfo.Version} (+https://github.com/yannb2/scratcharr)");
        return client;
    }
}
