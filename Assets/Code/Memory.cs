using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public sealed class Memory : IDisposable
{
    static double Time() => DateTime.Now.Ticks / (double)TimeSpan.TicksPerSecond;
    static readonly bool _debug = false;

    readonly string _path;
    readonly (string path, FileStream file) _lock;
    readonly MemoryMappedFile _memory;

    public Memory(string name, int capacity = int.MaxValue)
    {
        var map = $"bilirumine_{name}";
        _path = $"/dev/shm/{map}";
        var path = Path.Join(Application.streamingAssetsPath, "input", $"{name}.lock");
        var file = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _lock = (path, file);
        _memory = MemoryMappedFile.CreateFromFile(_path, FileMode.OpenOrCreate, map, capacity, MemoryMappedFileAccess.ReadWrite);
    }

    public async Task<byte[]> Read(int offset, int size)
    {
        await AcquireAsync();
        try
        {
            using var stream = _memory.CreateViewStream();
            var bytes = Pool<byte>.Take(size);
            stream.Seek(offset, SeekOrigin.Begin);
            await stream.ReadAsync(bytes);
            return bytes;
        }
        finally { await ReleaseAsync(); }
    }

    public async IAsyncEnumerable<byte[]> Read(int offset, int size, int count)
    {
        if (count <= 0) yield break;

        await AcquireAsync();
        try
        {
            using var stream = _memory.CreateViewStream();
            var each = size / count;
            for (int i = 0; i < count; i++)
            {
                var bytes = Pool<byte>.Take(each);
                var position = offset + i * each;
                stream.Seek(position, SeekOrigin.Begin);
                await stream.ReadAsync(bytes);
                yield return bytes;
            }
        }
        finally { await ReleaseAsync(); }
    }

    public unsafe bool Load(int offset, int size, Texture2D texture)
    {
        Acquire();
        try
        {
            using var access = _memory.CreateViewAccessor();
            try
            {
                var pointer = (byte*)IntPtr.Zero;
                access.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                if (pointer == null) throw new NullReferenceException();
                texture.LoadRawTextureData((IntPtr)(pointer + offset), size);
                texture.Apply();
                return true;
            }
            finally { access.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }
        finally { Release(); }
    }

    public unsafe bool Load(int offset, int size, AudioClip clip)
    {
        Acquire();
        try
        {
            using var access = _memory.CreateViewAccessor();
            try
            {
                var pointer = (byte*)IntPtr.Zero;
                access.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                if (pointer == null) throw new NullReferenceException();
                var span = new ReadOnlySpan<byte>(pointer + offset, size);
                return clip.SetData(MemoryMarshal.Cast<byte, float>(span), 0);
            }
            finally { access.SafeMemoryMappedViewHandle.ReleasePointer(); }
        }
        finally { Release(); }
    }

    public void Dispose()
    {
        try { _memory.Dispose(); } catch { }
        try { _lock.file.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }

    void Acquire()
    {
        while (true)
        {
            for (int i = 0; i < 10; i++)
            {
                for (int j = 0; j < 10; j++)
                {
                    try
                    {
                        _lock.file.Lock(0, 1);
                        Write($"C# ACQUIRE");
                        return;
                    }
                    catch (IOException) { }
                }
                Thread.Yield();
            }
            Thread.Sleep(1);
        }
    }

    async Task AcquireAsync()
    {
        while (true)
        {
            for (int i = 0; i < 10; i++)
            {
                for (int j = 0; j < 10; j++)
                {
                    try
                    {
                        _lock.file.Lock(0, 1);
                        await WriteAsync($"C# ACQUIRE");
                        return;
                    }
                    catch (IOException) { }
                }
                await Task.Yield();
            }
            await Task.Delay(1);
        }
    }

    void Release()
    {
        Write($"C# RELEASE");
        _lock.file.Unlock(0, 1);
    }

    async Task ReleaseAsync()
    {
        await WriteAsync($"C# RELEASE");
        _lock.file.Unlock(0, 1);
    }

    void Write(string message)
    {
        if (_debug)
        {
            _lock.file.Seek(0, SeekOrigin.End);
            _lock.file.Write(Encoding.UTF8.GetBytes($"[{Time()}] {message}{Environment.NewLine}"));
            _lock.file.Flush();
        }
    }

    async Task WriteAsync(string message)
    {
        if (_debug)
        {
            _lock.file.Seek(0, SeekOrigin.End);
            await _lock.file.WriteAsync(Encoding.UTF8.GetBytes($"[{Time()}] {message}{Environment.NewLine}"));
            await _lock.file.FlushAsync();
        }
    }
}