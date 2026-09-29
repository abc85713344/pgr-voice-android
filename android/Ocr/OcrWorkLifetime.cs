namespace PgrVoice.AndroidApp.Ocr;

/// <summary>
/// 将选择、初始化和推理放在同一个互斥区。停止使尚未进入推理的旧请求失效，
/// 并在在途推理之后释放；较新的请求会使旧释放操作失效，避免释放新模型。
/// </summary>
public sealed class OcrWorkLifetime
{
    readonly SemaphoreSlim gate = new(1, 1);
    long generation;

    public long CreateRequest() => Interlocked.Increment(ref generation);

    public async Task<T> RunAsync<T>(long request, Func<Task> prepare, Func<Task<T>> recognize,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireCurrent(request, cancellationToken);
            await prepare().ConfigureAwait(false);
            // Stop 可能在异步初始化中到达；不能在卸载之前再启动一次过期推理。
            RequireCurrent(request, cancellationToken);
            return await recognize().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public Task StopAsync(Action release, CancellationToken cancellationToken = default)
    {
        // 在调用者线程立即失效，不能等后台任务被调度后才变更代次。
        long stopRequest = Interlocked.Increment(ref generation);
        // gate 空闲时 WaitAsync 会同步完成；显式调度后台，保证模型 Dispose 不阻塞界面。
        return Task.Run(() => ReleaseWhenIdleAsync(stopRequest, release, cancellationToken));
    }

    async Task ReleaseWhenIdleAsync(long stopRequest, Action release, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref generation) == stopRequest) release();
        }
        finally { gate.Release(); }
    }

    void RequireCurrent(long request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref generation) != request)
            throw new OperationCanceledException("识别任务已被停止或替换。");
    }
}
