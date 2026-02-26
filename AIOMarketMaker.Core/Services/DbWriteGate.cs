namespace AIOMarketMaker.Core.Services;

public class DbWriteGate
{
    private readonly SemaphoreSlim _semaphore;

    public DbWriteGate(int maxConcurrent)
    {
        _semaphore = new SemaphoreSlim(maxConcurrent);
    }

    public Task WaitAsync() => _semaphore.WaitAsync();

    public void Release() => _semaphore.Release();
}
