using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace WPods.Service;

/// <summary>The service's named pipes (see <see cref="Protocol"/>): message mode, local interactive users only.</summary>
internal static class Pipes
{
    public static NamedPipeServerStream Create(string name, int maxInstances, int outBuffer)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, maxInstances, PipeTransmissionMode.Message, PipeOptions.Asynchronous, 1 << 17, outBuffer, security);
    }

    /// <summary>Reads one whole message into <paramref name="buffer"/>; 0 when the client closed. Throws when it does not fit.</summary>
    public static async Task<int> ReadMessageAsync(PipeStream pipe, byte[] buffer, CancellationToken cancel)
    {
        int total = 0;
        do
        {
            if (total == buffer.Length)
            {
                throw new InvalidDataException("message too large");
            }
            int n = await pipe.ReadAsync(buffer.AsMemory(total), cancel).ConfigureAwait(false);
            if (n == 0)
            {
                return 0;
            }
            total += n;
        }
        while (!pipe.IsMessageComplete);
        return total;
    }

    /// <summary>
    /// Accepts clients until <paramref name="stop"/>; <paramref name="outBuffer"/> is what a write may queue before it waits.
    /// </summary>
    public static async Task AcceptAsync(string name, int maxInstances, Func<NamedPipeServerStream, Task> serve, CancellationToken stop, int outBuffer = 1 << 17)
    {
        while (!stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = Create(name, maxInstances, outBuffer);
            }
            catch (IOException)
            {
                // All instances busy (the single-instance mic pipe is in use): try again shortly.
                await Task.Delay(1000, stop).ConfigureAwait(false);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                // Another program holds this pipe name with its own access rules: retry until it closes it.
                Log.Warn("pipe", $"{name}: the pipe name is taken by another program; retrying");
                await Task.Delay(5000, stop).ConfigureAwait(false);
                continue;
            }
            try
            {
                await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (stop.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await serve(pipe).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException or InvalidOperationException)
                {
                    Log.Info("pipe", $"{name}: client ended: {e.Message}");
                }
                finally
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }, CancellationToken.None);
        }
    }
}

/// <summary>\\.\pipe\WPods.Aap: one connection = one AAP channel to one AirPods.</summary>
internal sealed class AapPipeServer(HciProxy hci)
{
    // A small outgoing buffer (~12 mic packets): an app reading slower than real time makes the writes wait, so the
    // channel's bounded mic queue drops instead of the pipe holding seconds of audio (see AapChannel.Deliver).
    public Task RunAsync(CancellationToken stop) => Pipes.AcceptAsync(Protocol.AapPipe, NamedPipeServerStream.MaxAllowedServerInstances, p => ServeAsync(p, stop), stop, outBuffer: 4096);

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        var buffer = new byte[Protocol.MaxAapPacket + 16];
        int n;
        using (var hello = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            hello.CancelAfter(TimeSpan.FromSeconds(5));
            n = await Pipes.ReadMessageAsync(pipe, buffer, hello.Token).ConfigureAwait(false);
        }
        // Local change: 12 bytes = the 10-byte hello plus the u16 PSM to connect to (AAP when absent).
        if (n is not (10 or 12) || buffer[0] != (byte)'A' || buffer[1] != (byte)'P')
        {
            Log.Warn("aap", $"bad first message ({n} bytes)");
            return;
        }
        ulong address = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(2));
        ushort psm = n == 12 ? BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(10)) : HciProxy.AapPsm;
        // Reads on during the open, so an app that goes away cancels it and leaves the channel free.
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task<int> next = Pipes.ReadMessageAsync(pipe, buffer, stop);
        _ = next.ContinueWith(static (t, state) =>
        {
            _ = t.Exception; // observed here: the read may end after this method returned
            if (t.IsFaulted || t.IsCanceled || t.Result == 0)
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }, gone, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        (int error, AapChannel? ch) = await hci.OpenAsync(address, gone.Token, psm).ConfigureAwait(false);
        var reply = new byte[5];
        reply[0] = Protocol.AapOpenResult;
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(1), error);
        try
        {
            await pipe.WriteAsync(reply, stop).ConfigureAwait(false);
        }
        catch
        {
            if (ch is not null)
            {
                hci.Close(ch);
            }
            throw;
        }
        if (ch is null)
        {
            return;
        }
        // Inbound packets and the end of the channel go to the app; the pipe closes after the last message.
        Task writer = Task.Run(async () =>
        {
            try
            {
                await foreach (byte[] m in ch.Messages.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    await pipe.WriteAsync(m, CancellationToken.None).ConfigureAwait(false);
                    ch.Sent(m);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The app closed the pipe.
            }
            pipe.Dispose();
        }, CancellationToken.None);
        try
        {
            while (true)
            {
                n = await next.ConfigureAwait(false);
                if (n == 0 || n > Protocol.MaxAapPacket || !hci.Send(ch, buffer.AsSpan(0, n)))
                {
                    break;
                }
                next = Pipes.ReadMessageAsync(pipe, buffer, stop);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidDataException or OperationCanceledException)
        {
            // Pipe closed by either side.
        }
        finally
        {
            hci.Close(ch);
            await writer.ConfigureAwait(false);
        }
    }
}

/// <summary>\\.\pipe\WPods.Mic: the app's mic PCM and pair, and the capture state back. One client at a time.</summary>
internal sealed class MicPipeServer(Engine engine)
{
    private readonly Lock _lock = new();
    private NamedPipeServerStream? _client;

    public Task RunAsync(CancellationToken stop) => Pipes.AcceptAsync(Protocol.MicPipe, 1, p => ServeAsync(p, stop), stop);

    /// <summary>Tells the connected app whether an app records from the mic.</summary>
    public void NotifyCapture(bool capturing)
    {
        lock (_lock)
        {
            if (_client is { } c)
            {
                TrySend(c, capturing);
            }
        }
    }

    private static void TrySend(NamedPipeServerStream pipe, bool capturing)
    {
        try
        {
            pipe.Write([Protocol.MicCapture, capturing ? (byte)1 : (byte)0]);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The read loop sees the closed pipe.
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        lock (_lock)
        {
            _client = pipe;
            TrySend(pipe, engine.MicCapturing);
        }
        Log.Info("mic", "app connected");
        try
        {
            var buffer = new byte[1 + Protocol.MaxPcm];
            while (true)
            {
                int n = await Pipes.ReadMessageAsync(pipe, buffer, stop).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                if (buffer[0] == Protocol.MicPcm)
                {
                    engine.MicPush(buffer.AsSpan(1, n - 1));
                }
                else if (buffer[0] == Protocol.MicDevice && n >= 10)
                {
                    engine.SetMic(BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(1)), Encoding.UTF8.GetString(buffer, 10, n - 10), reported: buffer[9] == 1);
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                if (_client == pipe)
                {
                    _client = null;
                }
            }
            Log.Info("mic", "app disconnected");
        }
    }
}

/// <summary>\\.\pipe\WPods.Service: the service state and updates for the app, and the app's requests.</summary>
internal sealed class StatusPipeServer(Engine engine)
{
    private readonly Lock _lock = new();
    private readonly List<NamedPipeServerStream> _clients = [];

    // Local change: the outbound volume report (Protocol.LinkVolume).
    private const int VolumeIntervalMs = 2000;
    private Timer? _volumeTimer;
    private long _volumeAt;

    public Task RunAsync(CancellationToken stop)
    {
        _volumeAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _volumeTimer = new Timer(_ => BroadcastVolume(), null, VolumeIntervalMs, VolumeIntervalMs);
        stop.Register(() => _volumeTimer.Dispose());
        return Pipes.AcceptAsync(Protocol.StatusPipe, NamedPipeServerStream.MaxAllowedServerInstances, p => ServeAsync(p, stop), stop);
    }

    // The counts are taken every interval even without clients, so a report never covers more than one interval.
    private void BroadcastVolume()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long elapsed = (now - Interlocked.Exchange(ref _volumeAt, now)) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        List<(ulong Address, int Cid, long Bytes, int Pdus)> volume = engine.TakeOutVolume();
        lock (_lock)
        {
            if (_clients.Count == 0 || volume.Count == 0)
            {
                return;
            }
        }
        var m = new byte[5 + (18 * volume.Count)];
        m[0] = Protocol.LinkVolume;
        BinaryPrimitives.WriteUInt32LittleEndian(m.AsSpan(1), (uint)Math.Clamp(elapsed, 1, uint.MaxValue));
        for (int i = 0; i < volume.Count; i++)
        {
            Span<byte> entry = m.AsSpan(5 + (18 * i), 18);
            BinaryPrimitives.WriteUInt64LittleEndian(entry, volume[i].Address);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[8..], (ushort)volume[i].Cid);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[10..], (uint)Math.Min(volume[i].Bytes, uint.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[14..], (uint)volume[i].Pdus);
        }
        BroadcastMessage(m);
    }

    public void Broadcast(ServiceState state, string detail) => BroadcastMessage(Message(state, detail));

    private void BroadcastMessage(byte[] m)
    {
        lock (_lock)
        {
            foreach (NamedPipeServerStream c in _clients)
            {
                TrySend(c, m);
            }
        }
    }

    // Local change: this build has no updater. The message stays so a client that expects it still gets one:
    // status 0, auto-update off, no check done, only the installed version.
    private static byte[] UpdateMessage()
    {
        Version installed = typeof(StatusPipeServer).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return [Protocol.UpdateInfo, 0, 0, .. Encoding.UTF8.GetBytes($"{installed.ToString(3)}\0\0\0")];
    }

    private static byte[] Message(ServiceState state, string detail)
    {
        var m = new byte[2 + Encoding.UTF8.GetByteCount(detail)];
        m[0] = Protocol.Status;
        m[1] = (byte)state;
        Encoding.UTF8.GetBytes(detail, m.AsSpan(2));
        return m;
    }

    private static void TrySend(NamedPipeServerStream pipe, byte[] m)
    {
        try
        {
            pipe.Write(m);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The read loop sees the closed pipe.
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        lock (_lock)
        {
            _clients.Add(pipe);
            (ServiceState state, string detail) = engine.State;
            TrySend(pipe, Message(state, detail));
            TrySend(pipe, UpdateMessage());
        }
        try
        {
            var buffer = new byte[64];
            while (true)
            {
                int n = await Pipes.ReadMessageAsync(pipe, buffer, stop).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                switch (buffer[0])
                {
                    case Protocol.Retry:
                        engine.Retry();
                        break;
                    // Local change: 0x23 to 0x26 (auto-update, check, update now, app started) are ignored.
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                _clients.Remove(pipe);
            }
        }
    }
}
