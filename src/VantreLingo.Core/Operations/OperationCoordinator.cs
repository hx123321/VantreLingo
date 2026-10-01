// 从固定 STranslate TranslationResultCoordinator.cs 的操作编号与临界区发布机制裁剪派生。
// Copyright © 2022 zggsong. MIT 许可证见 licenses/STranslate.MIT.txt。
namespace VantreLingo.Core.Operations;

public sealed class OperationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private long _lastOperationId;
    private Operation? _current;
    private bool _disposed;

    public Operation Begin()
    {
        Operation? previous;
        Operation next;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            next = new Operation(++_lastOperationId);
            _current = next;
        }
        previous?.Cancel();
        return next;
    }

    public bool TryPublish(Operation operation, Action publish)
    {
        lock (_sync)
        {
            if (_disposed || !ReferenceEquals(_current, operation) || operation.Token.IsCancellationRequested)
                return false;
            // 验证所有权和副作用必须处于同一临界区，避免旧请求回写新结果。
            publish();
            return true;
        }
    }

    public void Cancel()
    {
        Operation? current;
        lock (_sync)
        {
            current = _current;
            _current = null;
        }
        current?.Cancel();
    }

    public void Dispose()
    {
        lock (_sync)
            _disposed = true;
        Cancel();
    }
}

public sealed class Operation : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _source = new();
    private bool _disposed;
    public long Id { get; }
    public CancellationToken Token { get; }

    internal Operation(long id)
    {
        Id = id;
        Token = _source.Token;
    }

    internal void Cancel()
    {
        lock (_sync)
            if (!_disposed)
                _source.Cancel();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _source.Cancel();
            _source.Dispose();
            _disposed = true;
        }
    }
}
