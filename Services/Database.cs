using System.Text;
using System.Text.Json;
using HotstarScraper.Models;
using MySqlConnector;

namespace HotstarScraper.Services
{
    /// <summary>
    /// MySQL storage. Titles are upserted (keyed by Hotstar's content id) as soon as each one is
    /// scraped, so a long run that stops halfway keeps its progress. Promotions are written last,
    /// in one transaction per run.
    /// </summary>
    public sealed class Database : IAsyncDisposable
    {
        private const int BatchSize = 200;
        private readonly MySqlConnection _connection;

        private Database(MySqlConnection connection) => _connection = connection;

        /// <summary>Connect, creating the database (if allowed) and the tables when they don't exist yet.</summary>
        public static async Task<Database> OpenAsync(string connectionString)
        {
            var builder = new MySqlConnectionStringBuilder(connectionString)
            {
                // Large batches of episodes are sent in one statement.
                CharacterSet = "utf8mb4",
                AllowUserVariables = true
            };

            string name = builder.Database;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("The connection string needs a database name, e.g. Database=hotstar");

            try
            {
                var server = new MySqlConnectionStringBuilder(builder.ConnectionString) { Database = "" };
                await using var bootstrap = new MySqlConnection(server.ConnectionString);
                await bootstrap.OpenAsync();
                await Exec(bootstrap, null, $"CREATE DATABASE IF NOT EXISTS `{name.Replace("`", "")}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            }
            catch (MySqlException)
            {
                // Hosted databases often forbid CREATE DATABASE; the database then has to exist already.
            }

            var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync();

            var db = new Database(connection);
            await db.EnsureSchemaAsync();
            return db;
        }

        private async Task EnsureSchemaAsync()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "db", "schema.sql");
            string sql = await File.ReadAllTextAsync(path);

            // Statements end with ';' at the end of a line; comments are dropped first.
            string clean = string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--")));
            foreach (string statement in clean.Split(";\n").Select(s => s.Trim().TrimEnd(';')).Where(s => s != ""))
                await Exec(_connection, null, statement);

            // CREATE TABLE IF NOT EXISTS silently keeps an older table with the same name; catch that here.
            var expected = new Dictionary<string, string>
            {
                ["movies"] = "cast_members", ["tv_shows"] = "season_count", ["seasons"] = "show_id",
                ["episodes"] = "season_id", ["promotions"] = "run_id"
            };
            foreach (var (table, column) in expected)
            {
                await using var cmd = new MySqlCommand(
                    "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t AND column_name = @c",
                    _connection);
                cmd.Parameters.AddWithValue("@t", table);
                cmd.Parameters.AddWithValue("@c", column);
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
                    throw new InvalidOperationException(
                        $"Table '{table}' exists but has a different layout (no '{column}' column). " +
                        $"Rename or drop the old table, e.g. RENAME TABLE {table} TO {table}_old;");
            }
        }

        // ---------- reads ----------

        /// <summary>When each movie and show was last scraped.</summary>
        public async Task<Dictionary<string, DateTime>> GetLastScrapedAsync()
        {
            var result = new Dictionary<string, DateTime>();
            await using var cmd = new MySqlCommand(
                "SELECT content_id, last_scraped_at FROM movies UNION ALL SELECT content_id, last_scraped_at FROM tv_shows", _connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result[reader.GetString(0)] = reader.GetDateTime(1);
            return result;
        }

        /// <summary>Number of stored episodes per season id.</summary>
        public async Task<Dictionary<string, int>> GetEpisodeCountsAsync()
        {
            var result = new Dictionary<string, int>();
            await using var cmd = new MySqlCommand("SELECT season_id, COUNT(*) FROM episodes GROUP BY season_id", _connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) result[reader.GetString(0)] = reader.GetInt32(1);
            return result;
        }

        // ---------- writes ----------

        public async Task SaveTitleAsync(TitleDetail title, DateTime now)
        {
            await using var tx = await _connection.BeginTransactionAsync();

            if (title is Movie m)
            {
                await UpsertAsync(tx, "movies",
                    new[] { "content_id", "title", "description", "release_year", "age_rating", "runtime_minutes", "imdb_rating",
                            "genres", "languages", "directors", "producers", "cast_members", "callouts",
                            "url", "image_url", "title_image_url", "first_seen_at", "last_scraped_at" },
                    new[] { new object?[] { m.ContentId, m.Title, m.Description, m.ReleaseYear, Null(m.AgeRating), m.RuntimeMinutes, m.ImdbRating,
                            J(m.Genres), J(m.Languages), J(m.Directors), J(m.Producers), J(m.Cast), J(m.Callouts),
                            m.Url, Null(m.ImageUrl), Null(m.TitleImageUrl), now, now } });
            }
            else if (title is TvShow s)
            {
                int? episodeTotal = s.Seasons.All(x => x.EpisodeCount is null) ? null : s.Seasons.Sum(x => x.EpisodeCount ?? 0);

                await UpsertAsync(tx, "tv_shows",
                    new[] { "content_id", "title", "description", "release_year", "age_rating", "imdb_rating", "season_count", "episode_count",
                            "genres", "languages", "creators", "directors", "producers", "cast_members", "callouts",
                            "url", "image_url", "title_image_url", "first_seen_at", "last_scraped_at" },
                    new[] { new object?[] { s.ContentId, s.Title, s.Description, s.ReleaseYear, Null(s.AgeRating), s.ImdbRating, s.Seasons.Count, episodeTotal,
                            J(s.Genres), J(s.Languages), J(s.Creators), J(s.Directors), J(s.Producers), J(s.Cast), J(s.Callouts),
                            s.Url, Null(s.ImageUrl), Null(s.TitleImageUrl), now, now } });

                await UpsertAsync(tx, "seasons",
                    new[] { "season_id", "show_id", "position", "season_number", "title", "episode_count", "first_seen_at", "last_scraped_at" },
                    s.Seasons.Select(x => new object?[] { x.SeasonId, x.ShowId, x.Position, x.SeasonNumber, x.Title, x.EpisodeCount, now, now }));

                await UpsertAsync(tx, "episodes",
                    new[] { "episode_id", "show_id", "season_id", "season_number", "episode_number", "title", "description",
                            "air_date", "runtime_minutes", "url", "image_url", "first_seen_at", "last_scraped_at" },
                    s.Seasons.SelectMany(x => x.Episodes).Select(e => new object?[] {
                            e.EpisodeId, e.ShowId, e.SeasonId, e.SeasonNumber, e.EpisodeNumber, e.Title, Null(e.Description),
                            e.AirDate?.ToDateTime(TimeOnly.MinValue), e.RuntimeMinutes, Null(e.Url), Null(e.ImageUrl), now, now }));
            }

            await tx.CommitAsync();
        }

        /// <summary>Write this run's promotions, linking each to its movie or show when that title is stored.</summary>
        public async Task SavePromotionsAsync(string runId, DateTime scrapedAt, IEnumerable<Promotion> promotions)
        {
            var movies = await IdsAsync("SELECT content_id FROM movies");
            var shows = await IdsAsync("SELECT content_id FROM tv_shows");

            await using var tx = await _connection.BeginTransactionAsync();
            await Exec(_connection, tx, "DELETE FROM promotions WHERE run_id = @p0", runId); // makes a re-save of the same run idempotent

            var rows = promotions.Select(p =>
            {
                ContentLink link = p.Link;
                string type = link.Type switch
                {
                    ContentType.Movie => "movie",
                    ContentType.Show => "show",
                    ContentType.Episode => "episode",
                    _ => "other"
                };
                string? movieId = link.Type == ContentType.Movie && movies.Contains(link.ContentId) ? link.ContentId : null;
                string? showId = link.ShowId != "" && shows.Contains(link.ShowId) ? link.ShowId : null;

                return new object?[] {
                    runId, scrapedAt, p.SectionType, Truncate(p.SectionTitle, 200), p.SectionPosition, p.Position, p.OnHome,
                    type, Null(link.ContentId), movieId, showId, Truncate(p.Title, 500), Null(p.Description),
                    J(p.Tags), J(p.Callouts), Null(p.Url), Null(p.ImageUrl) };
            });

            await UpsertAsync(tx, "promotions",
                new[] { "run_id", "scraped_at", "section_type", "section_title", "section_position", "position", "on_home",
                        "content_type", "content_id", "movie_id", "show_id", "title", "description",
                        "tags", "callouts", "url", "image_url" },
                rows);

            await tx.CommitAsync();
        }

        // ---------- data-quality queries over the whole database ----------

        /// <summary>Descriptions shared by more than one title (often fine: dubbed versions; worth a look otherwise).</summary>
        public Task<List<string>> FindSharedDescriptionsAsync() => ListAsync("""
            SELECT CONCAT(COUNT(*), ' titles: ', GROUP_CONCAT(CONCAT(title, ' [', content_id, ']') SEPARATOR ', '))
            FROM (SELECT content_id, title, description FROM movies
                  UNION ALL SELECT content_id, title, description FROM tv_shows) t
            WHERE CHAR_LENGTH(description) > 40
            GROUP BY description HAVING COUNT(*) > 1
            ORDER BY COUNT(*) DESC LIMIT 25
            """);

        /// <summary>Promotions in this run whose movie or show is missing from the database.</summary>
        public Task<List<string>> FindUnlinkedPromotionsAsync(string runId) => ListAsync("""
            SELECT CONCAT(section_title, ' #', position, ': ', title, ' [', content_id, ']')
            FROM promotions
            WHERE run_id = @p0
              AND ((content_type = 'movie' AND movie_id IS NULL)
                OR (content_type IN ('show', 'episode') AND show_id IS NULL))
            ORDER BY section_position, position
            """, runId);

        public async Task<Dictionary<string, long>> CountRowsAsync()
        {
            var counts = new Dictionary<string, long>();
            foreach (string table in new[] { "movies", "tv_shows", "seasons", "episodes", "promotions" })
            {
                await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {table}", _connection);
                counts[table] = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            return counts;
        }

        // ---------- helpers ----------

        /// <summary>
        /// Multi-row INSERT ... ON DUPLICATE KEY UPDATE in batches (one round trip per batch, which matters
        /// for a cloud database). first_seen_at is never overwritten.
        /// </summary>
        private async Task UpsertAsync(MySqlTransaction tx, string table, string[] columns, IEnumerable<object?[]> rows)
        {
            string update = string.Join(", ", columns
                .Where(c => c != "first_seen_at" && c != columns[0])
                .Select(c => $"{c} = VALUES({c})"));

            foreach (object?[][] batch in rows.Chunk(BatchSize))
            {
                var sql = new StringBuilder($"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES ");
                await using var cmd = new MySqlCommand { Connection = _connection, Transaction = tx };

                for (int r = 0; r < batch.Length; r++)
                {
                    if (r > 0) sql.Append(", ");
                    sql.Append('(');
                    for (int c = 0; c < columns.Length; c++)
                    {
                        if (c > 0) sql.Append(", ");
                        string name = $"@r{r}c{c}";
                        sql.Append(columns[c] is "genres" or "languages" or "directors" or "producers" or "creators"
                                   or "cast_members" or "callouts" or "tags" ? $"CAST({name} AS JSON)" : name);
                        cmd.Parameters.AddWithValue(name, batch[r][c] ?? DBNull.Value);
                    }
                    sql.Append(')');
                }

                sql.Append(" ON DUPLICATE KEY UPDATE ").Append(update);
                cmd.CommandText = sql.ToString();
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private static async Task Exec(MySqlConnection connection, MySqlTransaction? tx, string sql, params object?[] args)
        {
            await using var cmd = new MySqlCommand(sql, connection, tx);
            for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"@p{i}", args[i] ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<HashSet<string>> IdsAsync(string sql)
        {
            var ids = new HashSet<string>();
            await using var cmd = new MySqlCommand(sql, _connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
            return ids;
        }

        private async Task<List<string>> ListAsync(string sql, params object?[] args)
        {
            var list = new List<string>();
            await using var cmd = new MySqlCommand(sql, _connection);
            for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"@p{i}", args[i]);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) list.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            return list;
        }

        private static string J(List<string> values) => JsonSerializer.Serialize(values);

        private static string? Null(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
