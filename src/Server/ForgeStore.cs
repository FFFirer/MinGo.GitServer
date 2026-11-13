using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

record Repository(Guid Id, string Name, string DefaultBranch, string CreatedAt);
record GitToken(string Token, DateTimeOffset ExpiresAt);

sealed partial class ForgeStore
{
    private readonly string connectionString;
    private readonly ILogger<ForgeStore> logger;
    public string Root { get; }
    public string Repositories => Path.Combine(Root, "repos");
    public string Trash => Path.Combine(Root, "trash");
    public string Scratch => Path.Combine(Root, "scratch");
    public string RepositoryPath(Guid id) => Path.Combine(Repositories, $"{id:D}.git");
    public string Admin { get; }

    public ForgeStore(IConfiguration config, IWebHostEnvironment environment, ILogger<ForgeStore> logger)
    {
        this.logger = logger;
        Root = Path.GetFullPath(config["Forge:DataPath"] ?? "data", environment.ContentRootPath);
        Admin = config["Forge:AdminUser"] ?? "admin";
        if (!ValidSlug(Admin))
            throw new InvalidOperationException("Forge:AdminUser must be a lowercase URL slug.");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Root, "forge.db"),
            DefaultTimeout = 5
        }.ToString();
    }

    public FileStream LockDataDirectory()
    {
        Directory.CreateDirectory(Root);
        return File.Open(Path.Combine(Root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Initialize(string? password)
    {
        Directory.CreateDirectory(Repositories);
        Directory.CreateDirectory(Trash);
        Directory.CreateDirectory(Scratch);
        using var db = Open();
        using var version = Command(db, "PRAGMA user_version");
        var schemaVersion = (long)version.ExecuteScalar()!;
        if (schemaVersion > 1)
            throw new InvalidOperationException("The database was created by a newer version.");
        if (schemaVersion == 0)
        {
            using var schema = Command(db, """
                BEGIN;
                CREATE TABLE admin (id INTEGER PRIMARY KEY CHECK(id=1), username TEXT NOT NULL, password_hash TEXT NOT NULL);
                CREATE TABLE repos (id TEXT PRIMARY KEY, name TEXT NOT NULL, branch TEXT NOT NULL, created_at TEXT NOT NULL, deleted_at TEXT);
                CREATE UNIQUE INDEX active_repo_name ON repos(name) WHERE deleted_at IS NULL;
                CREATE TABLE token (id INTEGER PRIMARY KEY CHECK(id=1), hash BLOB NOT NULL, expires INTEGER NOT NULL);
                CREATE TABLE audit (id INTEGER PRIMARY KEY, action TEXT NOT NULL, repo_id TEXT, created_at TEXT NOT NULL);
                PRAGMA user_version = 1;
                COMMIT;
                """);
            schema.ExecuteNonQuery();
        }

        using var existing = Command(db, "SELECT username FROM admin WHERE id=1");
        var username = existing.ExecuteScalar() as string;
        if (username is null)
        {
            if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
                throw new InvalidOperationException("Set Forge__AdminPassword (at least 12 characters) for first startup.");
            var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<string>();
            using var seed = Command(db, "INSERT INTO admin VALUES(1,$user,$hash)",
                ("$user", Admin), ("$hash", hasher.HashPassword(Admin, password)));
            seed.ExecuteNonQuery();
        }
        else if (username != Admin)
        {
            throw new InvalidOperationException("Forge:AdminUser differs from the stored administrator.");
        }

        var known = new HashSet<Guid>();
        using (var repos = Command(db, "SELECT id,deleted_at FROM repos"))
        using (var rows = repos.ExecuteReader())
        {
            while (rows.Read())
            {
                var id = Guid.Parse(rows.GetString(0));
                known.Add(id);
                if (!rows.IsDBNull(1))
                    Quarantine(id);
            }
        }

        foreach (var path in Directory.EnumerateDirectories(Repositories, "*.git"))
        {
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) && !known.Contains(id))
                Quarantine(id);
        }
        foreach (var path in Directory.EnumerateDirectories(Scratch))
            Directory.Delete(path, recursive: true);
    }

    public void Quarantine(Guid id)
    {
        var path = RepositoryPath(id);
        if (Directory.Exists(path))
            Directory.Move(path, Path.Combine(Trash, $"{id:D}.git"));
    }

    public bool CheckPassword(string username, string password)
    {
        if (username != Admin || password.Length > 1024)
            return false;
        using var db = Open();
        using var query = Command(db, "SELECT password_hash FROM admin WHERE id=1");
        var hash = (string)query.ExecuteScalar()!;
        return new Microsoft.AspNetCore.Identity.PasswordHasher<string>()
            .VerifyHashedPassword(Admin, hash, password) != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed;
    }

    public GitToken IssueToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expires = DateTimeOffset.UtcNow.AddDays(90);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var insert = Command(db, "INSERT OR REPLACE INTO token VALUES(1,$hash,$expires)",
            ("$hash", Digest(token)), ("$expires", expires.ToUnixTimeSeconds()));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        WriteAudit(db, transaction, "token.rotate", null);
        transaction.Commit();
        return new(token, expires);
    }

    public bool CheckToken(string username, string token)
    {
        if (username != Admin || token.Length != 64)
            return false;
        using var db = Open();
        using var query = Command(db, "SELECT hash FROM token WHERE id=1 AND expires>$now",
            ("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        return query.ExecuteScalar() is byte[] hash && CryptographicOperations.FixedTimeEquals(hash, Digest(token));
    }

    private static byte[] Digest(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));

    public List<Repository> List()
    {
        using var db = Open();
        using var query = Command(db, "SELECT id,name,branch,created_at FROM repos WHERE deleted_at IS NULL ORDER BY name");
        using var rows = query.ExecuteReader();
        var result = new List<Repository>();
        while (rows.Read())
            result.Add(Read(rows));
        return result;
    }

    public Repository? Find(Guid id) => Find("id", id.ToString("D"));
    public Repository? Find(string name) => Find("name", name);

    private Repository? Find(string column, string value)
    {
        using var db = Open();
        using var query = Command(db, $"SELECT id,name,branch,created_at FROM repos WHERE {column}=$value AND deleted_at IS NULL",
            ("$value", value));
        using var rows = query.ExecuteReader();
        return rows.Read() ? Read(rows) : null;
    }

    public void Insert(Repository repo)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var insert = Command(db, "INSERT INTO repos VALUES($id,$name,$branch,$created,NULL)",
            ("$id", repo.Id.ToString("D")), ("$name", repo.Name), ("$branch", repo.DefaultBranch), ("$created", repo.CreatedAt));
        insert.Transaction = tx;
        insert.ExecuteNonQuery();
        WriteAudit(db, tx, "repo.create", repo.Id);
        tx.Commit();
    }

    public void Tombstone(Guid id)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var delete = Command(db, "UPDATE repos SET deleted_at=$now WHERE id=$id AND deleted_at IS NULL",
            ("$now", Now()), ("$id", id.ToString("D")));
        delete.Transaction = tx;
        if (delete.ExecuteNonQuery() == 1)
            WriteAudit(db, tx, "repo.delete", id);
        tx.Commit();
    }

    public void Audit(string action, Guid id)
    {
        try
        {
            using var db = Open();
            WriteAudit(db, null, action, id);
        }
        catch (SqliteException error)
        {
            logger.LogError(error, "Could not record {Action} for repository {RepositoryId}", action, id);
        }
    }

    private static void WriteAudit(SqliteConnection db, SqliteTransaction? transaction, string action, Guid? id)
    {
        using var insert = Command(db, "INSERT INTO audit(action,repo_id,created_at) VALUES($action,$id,$now)",
            ("$action", action), ("$id", (object?)id?.ToString("D") ?? DBNull.Value), ("$now", Now()));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static Repository Read(SqliteDataReader row) =>
        new(Guid.Parse(row.GetString(0)), row.GetString(1), row.GetString(2), row.GetString(3));

    public static string Now() => DateTimeOffset.UtcNow.ToString("O");

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();
    public static bool ValidSlug(string? value) => value is not null && Slug().IsMatch(value);
}
