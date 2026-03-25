using System.Runtime.InteropServices;
using Microsoft.Skype.Bots.Media;

namespace TeamsEchoBot.Media;

/// <summary>
/// Concrete implementation of AudioMediaBuffer for sending audio frames.
///
/// Per the official SDK docs: "The buffer object passed to Send() is still
/// potentially in-use after the method returns. The application must NOT free
/// the buffer's frame data until the buffer object's Dispose() method is
/// invoked by the Media Platform."
///
/// This class pins the managed byte[] and only unpins it when the SDK calls
/// Dispose() — not immediately after Send() returns.
/// </summary>
public class AudioSendMediaBuffer : AudioMediaBuffer
{
    private readonly byte[] _data;
    private GCHandle _handle;
    private bool _disposed;

    public AudioSendMediaBuffer(byte[] data, AudioFormat format, long timestamp)
    {
        _data = data;
        _handle = GCHandle.Alloc(data, GCHandleType.Pinned);

        Data      = _handle.AddrOfPinnedObject();
        Length    = (uint)data.Length;
        AudioFormat = format;
        Timestamp = timestamp;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle.IsAllocated)
            _handle.Free();
    }
}