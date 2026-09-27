namespace LabbyTwo.Storage;

/// <summary>
/// One cached value that a write can invalidate while a read is still loading it.
///
/// The stores all cache a table in memory and drop the copy on every write. The load is a
/// query, and a query takes time: a write that landed and dropped the cache while a load was
/// in flight used to be followed by that load storing what it had read — from before the
/// write. The cache then served the old rows until something else happened to be saved,
/// which on a quiet install could be days: a renamed connection that kept its old name, a
/// deleted alert rule that kept firing.
///
/// So every invalidation bumps a version. A loader notes the version before it queries and
/// stores its result only if nothing has invalidated since; if something has, the result is
/// still returned to that caller — it was true when read — but the next caller reads again.
/// </summary>
public sealed class VersionedCache<T> where T : class
{
    private readonly Lock _gate = new();
    private T? _value;
    private long _version;

    /// <summary>The cached value, or null when there is none.</summary>
    public T? Value
    {
        get
        {
            lock (_gate)
                return _value;
        }
    }

    /// <summary>Taken before loading, and handed back to <see cref="Store"/>.</summary>
    public long Version
    {
        get
        {
            lock (_gate)
                return _version;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _version++;
            _value = null;
        }
    }

    /// <summary>
    /// Keeps <paramref name="value"/> if nothing has invalidated since <paramref name="version"/>
    /// was taken, and returns it either way. The check and the store are one step under the
    /// lock; done as two, an invalidation could land between them and the stale value would
    /// be stored anyway.
    /// </summary>
    public T Store(T value, long version)
    {
        lock (_gate)
        {
            if (_version == version)
                _value = value;
        }
        return value;
    }
}
