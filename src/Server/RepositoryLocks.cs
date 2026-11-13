// Lifecycle leases allow concurrent Git operations while excluding repository removal.
sealed class RepositoryLocks
{
    private readonly Dictionary<Guid, int> leases = new();

    public IDisposable Read(Guid id) => Acquire(id, exclusive: false);
    public IDisposable Delete(Guid id) => Acquire(id, exclusive: true);

    private Lease Acquire(Guid id, bool exclusive)
    {
        lock (leases)
        {
            var count = leases.GetValueOrDefault(id);
            if (count < 0 || (exclusive && count > 0))
                throw new ForgeException(409, "Repository is busy. Try again.");
            leases[id] = exclusive ? -1 : count + 1;
            return new Lease(this, id);
        }
    }

    private void Release(Guid id)
    {
        lock (leases)
        {
            var count = leases[id];
            if (count <= 1)
                leases.Remove(id);
            else
                leases[id] = count - 1;
        }
    }

    private sealed class Lease(RepositoryLocks owner, Guid id) : IDisposable
    {
        public void Dispose() => owner.Release(id);
    }
}
