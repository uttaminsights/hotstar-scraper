using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;

// Hotstar dashboard: a small read-only web app over the scraper's MySQL database.
// Login comes from HOTSTAR_DB (same as the scraper). It listens on localhost only - it is for one person.

string connectionString = Environment.GetEnvironmentVariable("HOTSTAR_DB")
    ?? Environment.GetEnvironmentVariable("HOTSTAR_DB", EnvironmentVariableTarget.User)
    ?? "";
// Packaged (the exe from the GitHub release): wwwroot and the scraper sit next to the exe.
string exeDir = AppContext.BaseDirectory;
bool packaged = Directory.Exists(Path.Combine(exeDir, "wwwroot"));

if (connectionString == "")
{
    Console.Error.WriteLine("Set HOTSTAR_DB to the MySQL connection string first (the same one the scraper uses).");
    if (packaged) { Console.WriteLine("Press Enter to close."); Console.ReadLine(); }
    return 1;
}

// Double-clicked while the dashboard is already running: just open it again.
if (packaged)
{
    try
    {
        using var probe = new System.Net.Sockets.TcpClient();
        probe.Connect("127.0.0.1", 5080);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("http://localhost:5080") { UseShellExecute = true });
        return 0;
    }
    catch (System.Net.Sockets.SocketException) { /* not running yet - start it below */ }
}
var builder = WebApplication.CreateBuilder(packaged
    ? new WebApplicationOptions { Args = args, ContentRootPath = exeDir }
    : new WebApplicationOptions { Args = args });
string url = builder.Configuration["urls"] ?? "http://localhost:5080";
builder.WebHost.UseUrls(url);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var db = new Db(connectionString);

// ---------- Overview ----------
app.MapGet("/api/overview", async () =>
{
    var counts = (await db.QueryAsync("""
        SELECT (SELECT COUNT(*) FROM movies) movies, (SELECT COUNT(*) FROM tv_shows) shows,
               (SELECT COUNT(*) FROM seasons) seasons, (SELECT COUNT(*) FROM episodes) episodes,
               (SELECT ROUND(AVG(imdb_rating),1) FROM movies) avg_movie_rating,
               (SELECT ROUND(AVG(imdb_rating),1) FROM tv_shows) avg_show_rating,
               (SELECT MAX(scraped_at) FROM promotions) last_run_at,
               (SELECT COUNT(DISTINCT run_id) FROM promotions) runs
        """))[0];

    var topMovies = await db.QueryAsync("""
        SELECT content_id, title, description, release_year, age_rating, imdb_rating, runtime_minutes, genres, languages, image_url, title_image_url
        FROM movies WHERE imdb_rating IS NOT NULL ORDER BY imdb_rating DESC, release_year DESC LIMIT 20
        """);
    var topShows = await db.QueryAsync("""
        SELECT content_id, title, description, release_year, age_rating, imdb_rating, season_count, genres, languages, image_url, title_image_url
        FROM tv_shows WHERE imdb_rating IS NOT NULL ORDER BY imdb_rating DESC, season_count DESC LIMIT 20
        """);
    var newest = await db.QueryAsync("""
        SELECT * FROM (
          SELECT 'movie' kind, content_id, title, release_year, imdb_rating, languages, image_url FROM movies
          UNION ALL SELECT 'show', content_id, title, release_year, imdb_rating, languages, image_url FROM tv_shows
        ) x WHERE release_year IS NOT NULL ORDER BY release_year DESC, imdb_rating IS NULL, imdb_rating DESC LIMIT 20
        """);
    var biggestShows = await db.QueryAsync("""
        SELECT s.content_id, s.title, s.imdb_rating, COUNT(e.episode_id) episodes
        FROM tv_shows s JOIN episodes e ON e.show_id = s.content_id
        GROUP BY s.content_id, s.title, s.imdb_rating ORDER BY episodes DESC LIMIT 10
        """);

    // Hotstar puts real genres first and mood tags after them; the first two are the "main" genres.
    var genres = await db.QueryAsync("""
        SELECT g name, COUNT(*) n FROM (
          SELECT j.g FROM movies, JSON_TABLE(genres, '$[0 to 1]' COLUMNS (g VARCHAR(100) PATH '$')) j
          UNION ALL
          SELECT j.g FROM tv_shows, JSON_TABLE(genres, '$[0 to 1]' COLUMNS (g VARCHAR(100) PATH '$')) j
        ) x GROUP BY g ORDER BY n DESC LIMIT 12
        """);
    var languages = await db.QueryAsync("""
        SELECT l name, COUNT(*) n FROM (
          SELECT j.l FROM movies, JSON_TABLE(languages, '$[0]' COLUMNS (l VARCHAR(100) PATH '$')) j
          UNION ALL
          SELECT j.l FROM tv_shows, JSON_TABLE(languages, '$[0]' COLUMNS (l VARCHAR(100) PATH '$')) j
        ) x GROUP BY l ORDER BY n DESC
        """);
    var ratings = await db.QueryAsync("""
        SELECT FLOOR(imdb_rating) bucket, SUM(kind = 'movie') movies, SUM(kind = 'show') shows FROM (
          SELECT imdb_rating, 'movie' kind FROM movies WHERE imdb_rating IS NOT NULL
          UNION ALL SELECT imdb_rating, 'show' FROM tv_shows WHERE imdb_rating IS NOT NULL
        ) x GROUP BY bucket ORDER BY bucket
        """);

    return Results.Json(new
    {
        counts, topMovies, topShows, newest, biggestShows,
        genres = MergeLanguages(genres, isLanguage: false),
        languages = MergeLanguages(languages, isLanguage: true),
        ratings
    });
});

// ---------- Filter lists ----------
app.MapGet("/api/filters", async (string kind) =>
{
    string table = kind == "shows" ? "tv_shows" : "movies";
    var genres = await db.QueryAsync($"""
        SELECT j.g name, COUNT(*) n FROM {table}, JSON_TABLE(genres, '$[0 to 1]' COLUMNS (g VARCHAR(100) PATH '$')) j
        GROUP BY j.g ORDER BY n DESC
        """);
    var languages = await db.QueryAsync($"""
        SELECT j.l name, COUNT(*) n FROM {table}, JSON_TABLE(languages, '$[*]' COLUMNS (l VARCHAR(100) PATH '$')) j
        GROUP BY j.l ORDER BY n DESC
        """);
    return Results.Json(new
    {
        genres = genres.Select(r => (string)r["name"]!),
        languages = MergeLanguages(languages, isLanguage: true).Select(r => r["name"])
    });
});

// ---------- Movies & shows (same list endpoint, different table) ----------
app.MapGet("/api/titles", async (string kind, string? q, string? genre, string? lang, double? minRating,
                                 string? sort, int? page, int? size) =>
{
    bool shows = kind == "shows";
    string table = shows ? "tv_shows" : "movies";
    var where = new List<string>();
    var p = new Dictionary<string, object?>();

    if (!string.IsNullOrWhiteSpace(q))
    {
        where.Add("(title LIKE @q OR JSON_SEARCH(cast_members, 'one', @q) IS NOT NULL OR JSON_SEARCH(directors, 'one', @q) IS NOT NULL)");
        p["@q"] = $"%{q.Trim()}%";
    }
    if (!string.IsNullOrWhiteSpace(genre)) { where.Add("JSON_CONTAINS(genres, JSON_QUOTE(@genre))"); p["@genre"] = genre; }
    if (!string.IsNullOrWhiteSpace(lang))
    {
        // The site stores some languages in English and some in their own script; match either.
        where.Add("(JSON_CONTAINS(languages, JSON_QUOTE(@lang)) OR JSON_CONTAINS(languages, JSON_QUOTE(@langNative)))");
        p["@lang"] = lang;
        p["@langNative"] = Languages.ToNative(lang);
    }
    if (minRating is > 0) { where.Add("imdb_rating >= @minRating"); p["@minRating"] = minRating; }

    string order = sort switch
    {
        "year" => "release_year DESC, imdb_rating DESC",
        "title" => "title",
        "new" => "first_seen_at DESC, title",
        "episodes" when shows => "episode_count DESC",
        _ => "imdb_rating IS NULL, imdb_rating DESC, release_year DESC"
    };
    string filter = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
    int pageSize = Math.Clamp(size ?? 48, 1, 200);
    int offset = (Math.Max(page ?? 1, 1) - 1) * pageSize;

    string extra = shows
        ? ", season_count, (SELECT COUNT(*) FROM episodes e WHERE e.show_id = t.content_id) episode_count"
        : ", runtime_minutes";
    var items = await db.QueryAsync($"""
        SELECT * FROM (
          SELECT content_id, title, release_year, age_rating, imdb_rating, genres, languages, image_url, first_seen_at {extra}
          FROM {table} t {filter}
        ) x ORDER BY {order} LIMIT {pageSize} OFFSET {offset}
        """, p);
    long total = Convert.ToInt64((await db.QueryAsync($"SELECT COUNT(*) n FROM {table} {filter}", p))[0]["n"]);
    return Results.Json(new { total, items });
});

app.MapGet("/api/movies/{id}", async (string id) =>
{
    var rows = await db.QueryAsync("SELECT * FROM movies WHERE content_id = @id", new() { ["@id"] = id });
    if (rows.Count == 0) return Results.NotFound();
    var promos = await PromotionsFor(id);
    return Results.Json(new { movie = rows[0], promotions = promos });
});

app.MapGet("/api/shows/{id}", async (string id) =>
{
    var rows = await db.QueryAsync("SELECT * FROM tv_shows WHERE content_id = @id", new() { ["@id"] = id });
    if (rows.Count == 0) return Results.NotFound();
    var seasons = await db.QueryAsync("""
        SELECT s.season_id, s.season_number, s.title, s.episode_count announced,
               (SELECT COUNT(*) FROM episodes e WHERE e.season_id = s.season_id) `stored`
        FROM seasons s WHERE s.show_id = @id ORDER BY s.position
        """, new() { ["@id"] = id });
    var promos = await PromotionsFor(id);
    return Results.Json(new { show = rows[0], seasons, promotions = promos });
});

app.MapGet("/api/seasons/{id}/episodes", async (string id, int? page) =>
{
    int offset = (Math.Max(page ?? 1, 1) - 1) * 100;
    var items = await db.QueryAsync($"""
        SELECT episode_id, episode_number, title, description, air_date, runtime_minutes, url, image_url
        FROM episodes WHERE season_id = @id ORDER BY episode_number DESC, air_date DESC LIMIT 100 OFFSET {offset}
        """, new() { ["@id"] = id });
    return Results.Json(new { items });
});

// ---------- Episodes (latest aired) ----------
app.MapGet("/api/episodes", async (string? q, int? page) =>
{
    var p = new Dictionary<string, object?>();
    string filter = "";
    if (!string.IsNullOrWhiteSpace(q)) { filter = "WHERE e.title LIKE @q OR s.title LIKE @q"; p["@q"] = $"%{q.Trim()}%"; }
    int offset = (Math.Max(page ?? 1, 1) - 1) * 100;
    var items = await db.QueryAsync($"""
        SELECT e.episode_id, e.show_id, s.title show_title, e.season_number, e.episode_number, e.title,
               e.air_date, e.runtime_minutes, e.url, s.imdb_rating
        FROM episodes e JOIN tv_shows s ON s.content_id = e.show_id {filter}
        ORDER BY e.air_date IS NULL, e.air_date DESC, e.episode_number DESC LIMIT 100 OFFSET {offset}
        """, p);
    return Results.Json(new { items });
});

// ---------- Home page (promotions of one run) ----------
app.MapGet("/api/promotions", async (string? run) =>
{
    var runs = await db.QueryAsync("""
        SELECT run_id, MAX(scraped_at) scraped_at, COUNT(*) cards FROM promotions
        GROUP BY run_id ORDER BY run_id DESC LIMIT 30
        """);
    if (runs.Count == 0) return Results.Json(new { runs, sections = Array.Empty<object>() });
    string runId = run ?? (string)runs[0]["run_id"]!;
    var rows = await db.QueryAsync("""
        SELECT p.section_type, p.section_title, p.section_position, p.position, p.on_home, p.content_type,
               p.content_id, p.movie_id, p.show_id, p.title, p.tags, p.callouts, p.url, p.image_url,
               COALESCE(m.imdb_rating, s.imdb_rating) imdb_rating
        FROM promotions p
        LEFT JOIN movies m ON m.content_id = p.movie_id
        LEFT JOIN tv_shows s ON s.content_id = p.show_id
        WHERE p.run_id = @run ORDER BY p.section_position, p.position
        """, new() { ["@run"] = runId });
    var sections = rows.GroupBy(r => Convert.ToInt32(r["section_position"]))
        .Select(g => new { title = g.First()["section_title"], type = g.First()["section_type"], cards = g.ToList() });
    return Results.Json(new { run = runId, runs, sections });
});

// ---------- Scrape runs (started from the dashboard, run on this laptop) ----------
// Packaged: run scraper\HotstarScraper.exe and keep output in the user's AppData.
// From source: "dotnet run" the scraper project in the repo folder.
string repoRoot = packaged
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HotstarDashboard")
    : builder.Configuration["ScraperFolder"] ?? Directory.GetParent(app.Environment.ContentRootPath)!.FullName;
Directory.CreateDirectory(repoRoot);
string? scraperExe = packaged ? Path.Combine(exeDir, "scraper", "HotstarScraper.exe") : null;
var runner = new ScrapeRunner(repoRoot, scraperExe, connectionString);

app.MapGet("/api/run/status", () => Results.Json(runner.Status()));

app.MapPost("/api/run/start", (bool? full) =>
{
    string? problem = runner.Start(full == true);
    return problem is null ? Results.Json(runner.Status()) : Results.Conflict(new { error = problem });
});

app.MapPost("/api/run/stop", () => { runner.Stop(); return Results.Json(runner.Status()); });

// Past runs: every output folder that has a quality report.
app.MapGet("/api/runs", () =>
{
    string output = Path.Combine(repoRoot, "output");
    if (!Directory.Exists(output)) return Results.Json(Array.Empty<object>());
    var runs = Directory.GetDirectories(output)
        .Where(d => File.Exists(Path.Combine(d, "quality.txt")))
        .OrderByDescending(d => Path.GetFileName(d))
        .Take(30)
        .Select(d => new { folder = Path.GetFileName(d), quality = File.ReadAllText(Path.Combine(d, "quality.txt")) });
    return Results.Json(runs);
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine($"Hotstar dashboard is running at {url}  (close this window to stop it)");
    if (packaged)
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
});
app.Run();
return 0;

async Task<List<Dictionary<string, object?>>> PromotionsFor(string id) => await db.QueryAsync("""
    SELECT run_id, scraped_at, section_title, position, on_home FROM promotions
    WHERE content_id = @id OR movie_id = @id OR show_id = @id ORDER BY run_id DESC, section_position LIMIT 50
    """, new() { ["@id"] = id });

// Language rows may appear twice (English and native spelling): fold them into one English name.
static List<Dictionary<string, object?>> MergeLanguages(List<Dictionary<string, object?>> rows, bool isLanguage) =>
    rows.GroupBy(r => isLanguage ? Languages.ToEnglish((string)r["name"]!) : (string)r["name"]!)
        .Select(g => new Dictionary<string, object?> { ["name"] = g.Key, ["n"] = g.Sum(r => Convert.ToInt64(r["n"])) })
        .OrderByDescending(r => (long)r["n"]!)
        .ToList();

static class Languages
{
    private static readonly Dictionary<string, string> NativeToEnglish = new()
    {
        ["हिंदी"] = "Hindi", ["हिन्दी"] = "Hindi", ["తెలుగు"] = "Telugu", ["தமிழ்"] = "Tamil", ["বাংলা"] = "Bengali",
        ["मराठी"] = "Marathi", ["മലയാളം"] = "Malayalam", ["ಕನ್ನಡ"] = "Kannada", ["ગુજરાતી"] = "Gujarati",
        ["ਪੰਜਾਬੀ"] = "Punjabi", ["ଓଡ଼ିଆ"] = "Odia", ["অসমীয়া"] = "Assamese", ["भोजपुरी"] = "Bhojpuri",
        ["Español"] = "Spanish", ["Français"] = "French", ["한국어"] = "Korean", ["日本語"] = "Japanese"
    };

    public static string ToEnglish(string name) => NativeToEnglish.TryGetValue(name, out var e) ? e : name;

    public static string ToNative(string english) =>
        NativeToEnglish.FirstOrDefault(kv => kv.Value == english).Key ?? english;

    public static JsonNode? Translate(JsonNode? list)
    {
        if (list is not JsonArray arr) return list;
        return new JsonArray(arr.Select(n => (JsonNode?)JsonValue.Create(ToEnglish(n?.GetValue<string>() ?? ""))).Distinct().ToArray());
    }
}

/// <summary>Starts the scraper as a separate process and keeps its log, so the dashboard can show progress.</summary>
sealed class ScrapeRunner(string repoRoot, string? scraperExe, string connectionString)
{
    private readonly object gate = new();
    private System.Diagnostics.Process? process;
    private readonly List<string> lines = [];
    private DateTime? startedAt, endedAt;
    private int? exitCode;
    private bool full;
    private string? logFile;

    public string? Start(bool fullRun)
    {
        lock (gate)
        {
            if (process is { HasExited: false }) return "A run is already in progress.";
            string project = Path.Combine(repoRoot, "HotstarScraper.csproj");
            if (scraperExe is not null ? !File.Exists(scraperExe) : !File.Exists(project))
                return $"Scraper not found at {scraperExe ?? project}";

            Directory.CreateDirectory(Path.Combine(repoRoot, "output", "logs"));
            logFile = Path.Combine(repoRoot, "output", "logs", $"run-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            lines.Clear();
            startedAt = DateTime.Now; endedAt = null; exitCode = null; full = fullRun;

            var psi = new System.Diagnostics.ProcessStartInfo(scraperExe ?? "dotnet")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            if (scraperExe is null)
                foreach (string a in new[] { "run", "--project", project, "-c", "Release", "--" }) psi.ArgumentList.Add(a);
            if (fullRun) psi.ArgumentList.Add("--full");
            psi.Environment["HOTSTAR_DB"] = connectionString;

            process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Add(e.Data);
            process.ErrorDataReceived += (_, e) => Add(e.Data);
            process.Exited += (_, _) => { lock (gate) { endedAt = DateTime.Now; exitCode = process?.ExitCode; } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return null;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            Add("Stopped from the dashboard. Titles finished before this point are already saved.");
        }
    }

    private void Add(string? line)
    {
        if (line is null) return;
        lock (gate)
        {
            lines.Add(line);
            if (lines.Count > 2000) lines.RemoveRange(0, 500);
            if (logFile != null) File.AppendAllText(logFile, line + Environment.NewLine);
        }
    }

    public object Status()
    {
        lock (gate)
        {
            bool running = process is { HasExited: false };
            // "[12/649]" in the newest progress line tells how far the title pages are.
            string? progress = lines.LastOrDefault(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\[\d+/\d+\]"));
            var m = progress is null ? null : System.Text.RegularExpressions.Regex.Match(progress, @"\[(\d+)/(\d+)\]");
            return new
            {
                running, full, startedAt, endedAt, exitCode,
                done = m?.Success == true ? int.Parse(m.Groups[1].Value) : (int?)null,
                total = m?.Success == true ? int.Parse(m.Groups[2].Value) : (int?)null,
                log = lines.TakeLast(60).ToList()
            };
        }
    }
}

sealed class Db(string connectionString)
{
    private static readonly HashSet<string> JsonColumns =
        ["genres", "languages", "directors", "producers", "creators", "cast_members", "callouts", "tags"];

    public async Task<List<Dictionary<string, object?>>> QueryAsync(string sql, Dictionary<string, object?>? parameters = null)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        if (parameters != null)
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);

        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                string name = reader.GetName(i);
                object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (value is string s && JsonColumns.Contains(name))
                {
                    JsonNode? node = JsonNode.Parse(s);
                    value = name == "languages" ? Languages.Translate(node) : node;
                }
                else if (value is DateOnly d) value = d.ToString("yyyy-MM-dd");
                row[name] = value;
            }
            rows.Add(row);
        }
        return rows;
    }
}
