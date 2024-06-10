using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

public sealed class Memory : IDisposable
{
    readonly string _path;
    readonly string _lock;
    readonly MemoryMappedFile _memory;

    public Memory(string name, int capacity = int.MaxValue)
    {
        var file = $"bilirumine_{name}";
        _path = $"/dev/shm/{file}";
        _lock = Path.Join(Application.streamingAssetsPath, "input", $"{name}.lock");
        _memory = MemoryMappedFile.CreateFromFile(_path, FileMode.OpenOrCreate, file, capacity, MemoryMappedFileAccess.ReadWrite);
        Release();
    }

    public async Task<byte[]> Read(int offset, int size)
    {
        await Acquire(1);
        try
        {
            using var stream = _memory.CreateViewStream();
            var bytes = Pool<byte>.Take(size);
            stream.Seek(offset, SeekOrigin.Begin);
            await stream.ReadAsync(bytes);
            return bytes;
        }
        finally { Release(); }
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
        try { File.Delete(_path); } catch { }
        try { File.Delete(_lock); } catch { }
    }

    void Acquire()
    {
        while (true)
        {
            try { using (File.Open(_lock, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) break; }
            catch (IOException) { }
        }
    }

    async Task Acquire(int delay)
    {
        while (true)
        {
            try { using (File.Open(_lock, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) break; }
            catch (IOException) { await Task.Delay(delay); }
        }
    }

    void Release()
    {
        try { File.Delete(_lock); }
        catch (IOException) { }
    }
}