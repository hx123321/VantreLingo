// 从固定 STranslate Core/ISingleInstanceApp.cs 裁剪派生。
// Copyright © 2022 zggsong. MIT 许可证见 licenses/STranslate.MIT.txt。
// 保留 Mutex + named pipe；独立命名、限当前用户、显式取消、仅拥有者释放 Mutex。
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace VantreLingo.Desktop.Infrastructure;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName;
    private readonly bool _ownsMutex;
    public bool IsFirstInstance => _ownsMutex;

    public SingleInstance()
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法识别当前 Windows 用户。");
        _pipeName = $"VantreLingo-{identity}-{Process.GetCurrentProcess().SessionId}";
        _mutex = new Mutex(false, @"Local\" + _pipeName);
        try { _ownsMutex = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
    }

    public void SignalFirstInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1500);
            client.WriteByte(1);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // 第一实例可能尚在启动或正在退出；第二实例不能绕过单实例门。
        }
    }

    public void Listen(Action activate)
    {
        if (!_ownsMutex) throw new InvalidOperationException("只有第一实例可以监听。");
        _ = Task.Run(async () =>
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_shutdown.Token);
                    var signal = new byte[1];
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                    if (await server.ReadAsync(signal, readTimeout.Token) == 1 && signal[0] == 1)
                        activate();
                }
                catch (OperationCanceledException) { }
                catch (IOException)
                {
                    if (!_shutdown.IsCancellationRequested)
                        await Task.Delay(100, CancellationToken.None);
                }
            }
        });
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        if (_ownsMutex) _mutex.ReleaseMutex();
        _mutex.Dispose();
        // Listener 仍可能持有 Token，不提前 Dispose CancellationTokenSource。
    }
}
