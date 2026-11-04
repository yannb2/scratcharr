using Scratcharr.Clients;

namespace Scratcharr.Tests;

[TestClass]
public sealed class ClientParsingTests
{
    [TestMethod]
    public void Torrents_are_read_with_upper_case_hashes_and_clean_paths()
    {
        const string json = """
            [{"hash":"abc123","name":"Movie","category":"radarr","state":"stoppedUP","content_path":"/downloads/torrents/radarr/Movie/",
              "save_path":"/downloads/torrents/radarr/","total_size":1000,"progress":1,"seeding_time":3600,"seeding_time_limit":-2,
              "ratio":1.5,"ratio_limit":-1,"added_on":1700000000,"completion_on":-1}]
            """;
        var torrent = QbittorrentClient.ParseTorrents(json).Single();
        Assert.AreEqual("ABC123", torrent.Hash);
        Assert.AreEqual("/downloads/torrents/radarr/Movie", torrent.ContentPath);
        Assert.AreEqual("/downloads/torrents/radarr", torrent.SavePath);
        Assert.AreEqual(1000, torrent.Size);
        Assert.AreEqual(-2, torrent.SeedingLimitMinutes);
        Assert.AreEqual(-1, torrent.RatioLimit);
        Assert.IsTrue(torrent.Stopped);
        Assert.IsTrue(torrent.Finished);
        Assert.IsNotNull(torrent.Added);
        Assert.IsNull(torrent.Completed);
        Assert.AreEqual("Stopped", torrent.StateWord);
    }

    [TestMethod]
    public void Paused_is_stopped_too_for_qbittorrent_4()
    {
        var torrent = new Torrent { Hash = "A", Name = "x", State = "pausedUP" };
        Assert.IsTrue(torrent.Stopped);
    }

    [TestMethod]
    public void Preferences_give_the_seeding_limits_and_the_action()
    {
        var (limits, savePath) = QbittorrentClient.ParsePreferences(
            """{"max_seeding_time_enabled":true,"max_seeding_time":4320,"max_ratio_enabled":false,"max_ratio":2,"max_ratio_act":3,"save_path":"/downloads/torrents/"}""");
        Assert.AreEqual(4320L, limits.Minutes);
        Assert.IsNull(limits.Ratio);
        Assert.AreEqual(ShareLimitAction.RemoveWithFiles, limits.Action);
        Assert.AreEqual("/downloads/torrents", savePath);
    }

    [TestMethod]
    public void Categories_keep_their_save_paths()
    {
        var categories = QbittorrentClient.ParseCategories("""{"radarr":{"name":"radarr","savePath":""},"manual":{"name":"manual","savePath":"/downloads/torrents/manual"}}""");
        Assert.AreEqual("", categories["radarr"]);
        Assert.AreEqual("/downloads/torrents/manual", categories["manual"]);
    }

    [TestMethod]
    public void Sabnzbd_config_queue_and_history()
    {
        var config = SabnzbdClient.ParseConfig("""
            {"config":{"misc":{"complete_dir":"/downloads/usenet/complete/","download_dir":"/downloads/usenet/incomplete",
             "history_retention_option":"days-delete","history_retention_number":30},
             "categories":[{"name":"*","dir":""},{"name":"radarr","dir":"radarr"}]}}
            """);
        Assert.AreEqual("/downloads/usenet/complete", config.CompleteFolder);
        Assert.AreEqual("radarr", config.Categories["radarr"]);
        Assert.AreEqual("keeps 30 days, then deleted", config.HistoryRetention);

        var queue = SabnzbdClient.ParseQueue("""{"queue":{"slots":[{"nzo_id":"SABnzbd_nzo_1","filename":"Job","cat":"radarr","status":"Downloading","mb":"10"}]}}""",
            config.IncompleteFolder);
        Assert.AreEqual("/downloads/usenet/incomplete/Job", queue.Single().Path);
        Assert.IsTrue(queue.Single().InQueue);

        var history = SabnzbdClient.ParseHistory("""
            {"history":{"slots":[{"nzo_id":"SABnzbd_nzo_2","name":"Done","category":"sonarr","status":"Failed","storage":"/downloads/usenet/complete/sonarr/Done",
             "path":"/downloads/usenet/incomplete/Done","completed":1700000000,"bytes":5,"fail_message":"bad"}]}}
            """);
        var job = history.Single();
        Assert.IsTrue(job.Failed);
        Assert.AreEqual("/downloads/usenet/complete/sonarr/Done", job.Path);
        Assert.AreEqual("bad", job.FailMessage);
    }

    [TestMethod]
    public void Sabnzbd_old_retention_text()
    {
        Assert.AreEqual("keeps all", SabnzbdClient.ParseConfig("""{"config":{"misc":{"history_retention":"0"}}}""").HistoryRetention);
        Assert.AreEqual("keeps 7 days", SabnzbdClient.ParseConfig("""{"config":{"misc":{"history_retention":"7d"}}}""").HistoryRetention);
    }

    [TestMethod]
    public void Arr_queue_history_and_clients()
    {
        var queue = ArrClient.ParseQueue("""
            {"records":[{"downloadId":"abc","title":"Movie","trackedDownloadState":"importPending","outputPath":"/downloads/torrents/radarr/Movie/",
             "statusMessages":[{"title":"Movie.mkv","messages":["No files found are eligible for import"]}]}]}
            """);
        Assert.AreEqual("ABC", queue.Single().DownloadId);
        Assert.AreEqual("/downloads/torrents/radarr/Movie", queue.Single().OutputPath);
        CollectionAssert.Contains(queue.Single().Messages.ToList(), "No files found are eligible for import");

        var history = ArrClient.ParseHistory("""
            {"records":[{"eventType":"downloadFolderImported","downloadId":"abc","sourceTitle":"Movie","date":"2026-11-01T10:00:00Z",
             "data":{"droppedPath":"/downloads/torrents/radarr/Movie/Movie.mkv","importedPath":"/movies/Movie (2024)/Movie (2024).mkv","downloadClientName":"qBittorrent"}}]}
            """);
        Assert.AreEqual(ArrEventKind.Imported, history.Single().Kind);
        Assert.AreEqual(DateTimeKind.Utc, history.Single().Date.Kind);
        Assert.AreEqual(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc), history.Single().Date);

        var clients = ArrClient.ParseClients("""
            [{"name":"qBittorrent","implementation":"QBittorrent","enable":true,"removeCompletedDownloads":true,"removeFailedDownloads":false,
              "fields":[{"name":"host","value":"gluetun"},{"name":"port","value":8080},{"name":"movieCategory","value":"radarr"},{"name":"movieImportedCategory","value":""}]}]
            """);
        var client = clients.Single();
        Assert.AreEqual("radarr", client.Category);
        Assert.AreEqual("gluetun:8080", client.Host);
        Assert.IsTrue(client.RemoveCompleted);
        Assert.IsFalse(client.RemoveFailed);
    }

    [TestMethod]
    public void Qbittorrent_api_key_is_sent_as_a_bearer_token()
    {
        using var withKey = QbittorrentClient.NewClient(new() { Url = "http://gluetun:8080", ApiKey = " qbt_abc " }, new System.Net.CookieContainer());
        Assert.AreEqual("Bearer", withKey.DefaultRequestHeaders.Authorization!.Scheme);
        Assert.AreEqual("qbt_abc", withKey.DefaultRequestHeaders.Authorization.Parameter);

        using var withLogin = QbittorrentClient.NewClient(new() { Url = "http://gluetun:8080", Username = "u", Password = "p" }, new System.Net.CookieContainer());
        Assert.IsNull(withLogin.DefaultRequestHeaders.Authorization);
    }

    [TestMethod]
    public void Lidarr_uses_api_v1()
    {
        Assert.AreEqual("api/v1", ArrClient.ApiBase(ArrApp.Lidarr));
        Assert.AreEqual("api/v3", ArrClient.ApiBase(ArrApp.Radarr));
    }
}
