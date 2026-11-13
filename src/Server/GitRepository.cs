using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

record CreateRepository(string Name, string? DefaultBranch = null);
record CreateReference(string Name, string Sha);
record GitReference(string Name, string Sha);
record GitCommit(string Sha, string Parents, string Author, string Email, string AuthoredAt, string Message);
record TreeEntry(string Name, string Kind, string Sha, string Mode);
record GitTree(string? Head, TreeEntry[] Entries);
record GitBlob(string Head, string Sha, int Size, string? Content);
record FileChange(string Branch, string Path,
    [property: System.Text.Json.Serialization.JsonRequired] string? ExpectedHead, string? Content, string Message);
record FileCommit(string Sha);
record GitDiff(string Text);

sealed partial class GitRepository(ForgeStore store, GitProcess git, RepositoryLocks locks)
{
    private const int FileLimit = 1024 * 1024;
    private const string Missing = "0000000000000000000000000000000000000000";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<Repository> Create(CreateRepository request, CancellationToken token)
    {
        var branch = request.DefaultBranch ?? "main";
        if (!ForgeStore.ValidSlug(request.Name))
            throw new ForgeException(422, "Use 1–63 lowercase letters, digits, underscores or hyphens for the repository name.");
        await ValidateRef("heads", branch, token);
        var repo = new Repository(Guid.NewGuid(), request.Name, branch, ForgeStore.Now());
        var path = store.RepositoryPath(repo.Id);
        try
        {
            await git.Run(null, ["init", "--bare", "--object-format=sha1", "--template=", $"--initial-branch={branch}", "--", path], token);
            await Run(repo, ["config", "receive.denyDeleteCurrent", "true"], token);
            await Run(repo, ["config", "receive.maxInputSize", "268435456"], token);
            await Run(repo, ["config", "receive.fsckObjects", "true"], token);
            store.Insert(repo);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteExtendedErrorCode == 2067)
        {
            store.Quarantine(repo.Id);
            throw new ForgeException(409, "Repository name already exists.");
        }
        catch
        {
            store.Quarantine(repo.Id);
            throw;
        }
        return repo;
    }

    public void Delete(Guid id)
    {
        using var lease = locks.Delete(id);
        Get(id);
        store.Tombstone(id);
        store.Quarantine(id);
    }

    public Repository Get(Guid id) => store.Find(id) ?? throw new ForgeException(404, "Repository does not exist.");

    public async Task<GitReference[]> References(Guid id, string kind, CancellationToken token)
    {
        using var lease = locks.Read(id);
        var output = await Run(Get(id), ["for-each-ref", "--format=%(refname:lstrip=2)%00%(objectname)", $"refs/{kind}/"], token);
        return GitProcess.Decode(output).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\0')).Select(fields => new GitReference(fields[0], fields[1])).ToArray();
    }

    public async Task<GitReference> AddReference(Guid id, string kind, CreateReference request, CancellationToken token)
    {
        using var lease = locks.Read(id);
        var repo = Get(id);
        var reference = await ValidateRef(kind, request.Name, token);
        var sha = await Resolve(repo, RequireSha(request.Sha), token)
            ?? throw new ForgeException(404, "Commit does not exist.");
        try { await Run(repo, ["update-ref", reference, sha, Missing], token); }
        catch (ForgeException e) when (e.Status == 422)
        {
            throw new ForgeException(409, "Reference already exists or conflicts with another reference.");
        }
        store.Audit($"ref.{kind}.create", id);
        return new(request.Name, sha);
    }

    public async Task RemoveReference(Guid id, string kind, string name, CancellationToken token)
    {
        using var lease = locks.Read(id);
        var repo = Get(id);
        var reference = await ValidateRef(kind, name, token);
        if (kind == "heads" && name == repo.DefaultBranch)
            throw new ForgeException(409, "The default branch cannot be deleted.");
        var old = GitProcess.Decode(await Run(repo, ["show-ref", "--hash", "--verify", reference], token, allowFailure: true)).Trim();
        if (old.Length == 0)
            throw new ForgeException(404, "Reference does not exist.");
        try { await Run(repo, ["update-ref", "-d", reference, old], token); }
        catch (ForgeException e) when (e.Status == 422)
        {
            throw new ForgeException(409, "Reference changed. Refresh and try again.");
        }
        store.Audit($"ref.{kind}.delete", id);
    }

    public async Task<GitCommit[]> History(Guid id, string? branch, string? tag, int? skip, int? count, CancellationToken token)
    {
        using var lease = locks.Read(id);
        var repo = Get(id);
        var head = await Head(repo, branch, tag, token);
        if (head is null)
            return [];
        return ParseLog(await Run(repo, ["log", "-z", "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%B",
            $"--skip={Math.Clamp(skip ?? 0, 0, 1000000)}", $"-{Math.Clamp(count ?? 25, 1, 100)}", head, "--"], token));
    }

    public async Task<GitCommit> Commit(Guid id, string sha, CancellationToken token)
    {
        using var lease = locks.Read(id);
        var repo = Get(id);
        var commit = await Resolve(repo, RequireSha(sha), token) ?? throw new ForgeException(404, "Commit does not exist.");
        return ParseLog(await Run(repo, ["log", "-1", "-z", "--format=%H%x00%P%x00%an%x00%ae%x00%aI%x00%B", commit, "--"], token))[0];
    }

    private static GitCommit[] ParseLog(byte[] output)
    {
        var fields = GitProcess.Decode(output).Split('\0');
        var commits = new List<GitCommit>();
        for (var i = 0; i + 5 < fields.Length; i += 6)
            commits.Add(new(fields[i], fields[i + 1], fields[i + 2], fields[i + 3], fields[i + 4], fields[i + 5].TrimEnd('\n')));
        return commits.ToArray();
    }

    public async Task<GitDiff> Diff(Guid id, string sha, string? path, CancellationToken token)
    {
        using var lease = locks.Read(id);
        var repo = Get(id);
        var head = await Resolve(repo, RequireSha(sha), token) ?? throw new ForgeException(404, "Commit does not exist.");
        var args = new List<string> { "show", "--format=", "--first-parent", "--diff-merges=first-parent", "--no-ext-diff", "--no-textconv", "--no-color", head, "--" };
        if (path is not null)
            args.Add(RequirePath(path));
        return new(GitProcess.Decode(await Run(repo, args.ToArray(), token)));
    }

    public async Task<GitTree> Tree(Guid id, string? branch, string? tag, string? path, string? sha, CancellationToken token)
    {
        using var lease = locks.Read(id);
        var repo = Get(id);
        var head = await Snapshot(repo, branch, tag, sha, token);
        if (head is null)
            return new(null, []);
        var tree = head;
        if (!string.IsNullOrEmpty(path))
        {
            var entry = await Entry(repo, head, RequirePath(path), token);
            if (entry is not { Kind: "tree" })
                throw new ForgeException(404, "Directory does not exist.");
            tree = entry.Sha;
        }
        var entries = await Run(repo, ["ls-tree", "-z", tree], token);
        return new(head, ParseTree(entries));
    }

    public async Task<GitBlob> Blob(Guid id, string? branch, string? tag, string path, string? sha, CancellationToken token)
    {
        using var lease = locks.Read(id);
        var repo = Get(id);
        var head = await Snapshot(repo, branch, tag, sha, token) ?? throw new ForgeException(404, "File does not exist.");
        var entry = await Entry(repo, head, RequirePath(path), token);
        if (entry is not { Kind: "blob" })
            throw new ForgeException(404, "File does not exist.");
        var size = long.Parse(GitProcess.Decode(await Run(repo, ["cat-file", "-s", entry.Sha], token)), CultureInfo.InvariantCulture);
        if (size > FileLimit)
            throw new ForgeException(413, "File exceeds 1 MiB.");
        var bytes = await Run(repo, ["cat-file", "blob", entry.Sha], token, limit: FileLimit);
        string? content = null;
        if (!bytes.Contains((byte)0))
        {
            try { content = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { }
        }
        return new(head, entry.Sha, (int)size, content);
    }

    public async Task<FileCommit> Edit(Guid id, FileChange request, bool delete, CancellationToken token)
    {
        using var lease = locks.Read(id);
        var repo = Get(id);
        var reference = await ValidateRef("heads", request.Branch, token);
        var path = RequirePath(request.Path);
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 4096 || request.Message.Contains('\0'))
            throw new ForgeException(422, "Enter a commit message of up to 4096 characters.");
        var expected = request.ExpectedHead is null ? null : RequireSha(request.ExpectedHead);
        var current = await Resolve(repo, reference, token);
        if (expected != current)
            throw new ForgeException(409, "Branch HEAD changed. Refresh before committing.");
        if (current is null && (request.Branch != repo.DefaultBranch || delete))
            throw new ForgeException(404, "Branch does not exist.");
        var entry = current is null ? null : await Entry(repo, current, path, token);
        if (delete && entry is null)
            throw new ForgeException(404, "File does not exist.");
        if (entry is not null && entry.Mode is not ("100644" or "100755"))
            throw new ForgeException(422, "Only regular files can be edited.");

        byte[]? content = null;
        if (!delete)
        {
            if (request.Content is null || request.Content.Contains('\0'))
                throw new ForgeException(422, "Provide UTF-8 text.");
            try { content = StrictUtf8.GetBytes(request.Content); }
            catch (EncoderFallbackException) { throw new ForgeException(422, "Provide valid UTF-8 text."); }
            if (content.Length > FileLimit)
                throw new ForgeException(413, "File exceeds 1 MiB.");
        }

        var directory = Directory.CreateDirectory(Path.Combine(store.Scratch, Guid.NewGuid().ToString("N")));
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = Path.Combine(directory.FullName, "index"),
                ["GIT_AUTHOR_NAME"] = store.Admin,
                ["GIT_AUTHOR_EMAIL"] = $"{store.Admin}@localhost",
                ["GIT_COMMITTER_NAME"] = store.Admin,
                ["GIT_COMMITTER_EMAIL"] = $"{store.Admin}@localhost"
            };
            await Run(repo, current is null ? ["read-tree", "--empty"] : ["read-tree", current], token, environment: environment);
            if (delete)
            {
                await Run(repo, ["update-index", "-z", "--index-info"], token,
                    Encoding.UTF8.GetBytes($"0 {Missing}\t{path}\0"), environment);
            }
            else
            {
                var blob = GitProcess.Decode(await Run(repo, ["hash-object", "-w", "--stdin"], token, content)).Trim();
                await Run(repo, ["update-index", "--add", "--cacheinfo", entry?.Mode ?? "100644", blob, path], token, environment: environment);
            }
            var tree = GitProcess.Decode(await Run(repo, ["write-tree"], token, environment: environment)).Trim();
            var args = current is null ? new[] { "commit-tree", tree } : ["commit-tree", tree, "-p", current];
            var sha = GitProcess.Decode(await Run(repo, args, token, Encoding.UTF8.GetBytes(request.Message), environment)).Trim();
            try { await Run(repo, ["update-ref", reference, sha, current ?? Missing], token); }
            catch (ForgeException e) when (e.Status == 422)
            {
                if (await Resolve(repo, reference, token) != current)
                    throw new ForgeException(409, "Branch HEAD changed. Refresh before committing.");
                throw new ForgeException(409, "Branch could not be updated. Retry after refreshing.");
            }
            store.Audit(delete ? "file.delete" : "file.write", id);
            return new(sha);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task<TreeEntry?> Entry(Repository repo, string head, string path, CancellationToken token)
    {
        return ParseTree(await Run(repo, ["ls-tree", "-z", head, "--", path], token))
            .SingleOrDefault(entry => entry.Name == path);
    }

    private static TreeEntry[] ParseTree(byte[] bytes) => GitProcess.Decode(bytes)
        .Split('\0', StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            var tab = line.IndexOf('\t');
            var fields = line[..tab].Split(' ');
            return new TreeEntry(line[(tab + 1)..], fields[1], fields[2], fields[0]);
        }).ToArray();

    private async Task<string?> Snapshot(Repository repo, string? branch, string? tag, string? sha, CancellationToken token) =>
        sha is null ? await Head(repo, branch, tag, token) :
            await Resolve(repo, RequireSha(sha), token) ?? throw new ForgeException(404, "Commit does not exist.");

    private async Task<string?> Head(Repository repo, string? branch, string? tag, CancellationToken token)
    {
        if (branch is not null && tag is not null)
            throw new ForgeException(422, "Choose either a branch or a tag.");
        var reference = tag is null
            ? await ValidateRef("heads", branch ?? repo.DefaultBranch, token)
            : await ValidateRef("tags", tag, token);
        var head = await Resolve(repo, reference, token);
        if (head is null && (tag is not null || (branch is not null && branch != repo.DefaultBranch)))
            throw new ForgeException(404, "Reference does not exist or does not point to a commit.");
        return head;
    }

    private async Task<string?> Resolve(Repository repo, string reference, CancellationToken token)
    {
        var sha = GitProcess.Decode(await Run(repo,
            ["rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}"], token, allowFailure: true)).Trim();
        return sha.Length == 0 ? null : sha;
    }

    private async Task<string> ValidateRef(string kind, string? name, CancellationToken token)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 240 || name.StartsWith('-') || name.Contains('\0'))
            throw new ForgeException(422, "Invalid reference name.");
        var reference = $"refs/{kind}/{name}";
        await git.Run(null, ["check-ref-format", reference], token);
        return reference;
    }

    private static string RequireSha(string? sha) => sha is not null && ObjectId().IsMatch(sha)
        ? sha.ToLowerInvariant() : throw new ForgeException(422, "Provide a full Git commit SHA.");

    private static string RequirePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || Encoding.UTF8.GetByteCount(path) > 4096 ||
            path.Contains('\\') || path.Contains('\0') ||
            path.Split('/').Any(part => part is "" or "." or ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new ForgeException(422, "Invalid repository path.");
        return path;
    }

    private Task<byte[]> Run(Repository repo, string[] args, CancellationToken token,
        byte[]? input = null, Dictionary<string, string>? environment = null, int limit = 2 * 1024 * 1024,
        bool allowFailure = false) => git.Run(store.RepositoryPath(repo.Id), args, token, input, environment, limit, allowFailure);

    [GeneratedRegex(@"\A[0-9a-fA-F]{40}\z")]
    private static partial Regex ObjectId();
}
