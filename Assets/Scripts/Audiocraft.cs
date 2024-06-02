#nullable enable

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class Audiocraft
{
    public sealed record State
    {
        static int _version = 0;

        public static int Reserve() => Interlocked.Increment(ref _version);

        public int Version;
        public bool Loop;
        public int Offset;
        public int Size;
        public int Generation;
        public string[]? Prompts;
        public int[]? Cancel;
        public int[]? Pause;
        public int[]? Resume;

        public override string ToString() => $@"{{""version"":{Version},""loop"":{(Loop ? "True" : "False")},""offset"":{Offset},""size"":{Size},""generation"":{Generation},""prompts"":[{string.Join(",", Prompts ?? Array.Empty<string>())}],""cancel"":[{string.Join(",", Cancel ?? Array.Empty<int>())}],""pause"":[{string.Join(",", Pause ?? Array.Empty<int>())}],""resume"":[{string.Join(",", Resume ?? Array.Empty<int>())}]}}";
    }

    public sealed record Clip
    {
        public int Version;
        public int Rate;
        public int Samples;
        public int Channels;
        public int Index;
        public int Count;
        public int Offset;
        public int Size;
        public int Generation;
    }

    public static (Process process, MemoryMappedFile memory) Create() => (Utility.Docker("audiocraft"), Utility.Memory("audio"));

    public static bool Load(this MemoryMappedFile memory, int rate, int samples, int channels, int offset, ref AudioClip? clip)
    {
        if (clip == null || clip.samples != samples || clip.channels != channels)
            clip = AudioClip.Create("audio", samples, channels, rate, false);

        using (var access = memory.CreateViewAccessor())
        {
            unsafe
            {
                var pointer = (byte*)IntPtr.Zero;
                try
                {
                    access.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                    if (pointer == null)
                    {
                        Error("Failed to acquire pointer to shared memory.");
                        return false;
                    }
                    clip.SetData(new ReadOnlySpan<float>(pointer + offset, samples * channels), 0);
                }
                finally { access.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
        }
        return true;
    }


    public static int Write(Process process, Func<int, State> get)
    {
        var version = State.Reserve();
        var state = get(version);
        Log($"Sending input '{state}'.");
        process.StandardInput.WriteLine($"{state}");
        process.StandardInput.Flush();
        return version;
    }

    public static async Task Read(Process process, ConcurrentQueue<Clip> clips)
    {
        while (!process.HasExited)
        {
            var line = await process.StandardOutput.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var index = 0;
                var splits = line.Split(",", StringSplitOptions.None);
                var version = int.Parse(splits[index++]);
                var rate = int.Parse(splits[index++]);
                var samples = int.Parse(splits[index++]);
                var channels = int.Parse(splits[index++]);
                var count = int.Parse(splits[index++]);
                var offset = int.Parse(splits[index++]);
                var size = int.Parse(splits[index++]) / count;
                var generation = int.Parse(splits[index++]);
                var clip = new Clip
                {
                    Version = version,
                    Rate = rate,
                    Samples = samples,
                    Channels = channels,
                    Count = count,
                    Offset = offset,
                    Size = size,
                    Generation = generation,
                };
                Log($"Received clip: {clip}");
                for (int i = 0; i < count; i++, offset += size)
                    clips.Enqueue(clip with { Index = i, Offset = offset });
            }
            catch (FormatException) { Warn(line); }
            catch (IndexOutOfRangeException) { Warn(line); }
            catch (Exception exception) { Debug.LogException(exception); }
        }
    }

    public static void Log(string message) => Debug.Log($"AUDIOCRAFT: {message}");
    public static void Warn(string message) => Debug.LogWarning($"AUDIOCRAFT: {message}");
    public static void Error(string message) => Debug.LogError($"AUDIOCRAFT: {message}");
}