#nullable enable

using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class Audiocraft
{
    [Serializable]
    public sealed record State
    {
        static int _version = 0;

        public static int Reserve() => Interlocked.Increment(ref _version);

        public int Version;
        public Tags Tags;
        public bool Loop;
        public bool Empty;
        public int Offset;
        public int Size;
        public int Generation;
        public float Duration;
        public float Overlap;
        public string Data = "";
        public string[] Prompts = Array.Empty<string>();
        public int[] Cancel = Array.Empty<int>();
        public int[] Pause = Array.Empty<int>();
        public int[] Resume = Array.Empty<int>();

        public override string ToString() => $@"{{
""version"":{Version},
""tags"":{(int)Tags},
""loop"":{(Loop ? "True" : "False")},
""empty"":{(Empty ? "True" : "False")},
""offset"":{Offset},
""size"":{Size},
""generation"":{Generation},
""duration"":{Duration},
""overlap"":{Overlap},
""data"":""{Data}"",
""prompts"":[{string.Join(",", Prompts.Select(prompt => $@"""{prompt.Escape()}"""))}],
""cancel"":[{string.Join(",", Cancel)}],
""pause"":[{string.Join(",", Pause)}],
""resume"":[{string.Join(",", Resume)}]
}}".Replace("\n", "").Replace("\r", "");
    }

    [Serializable]
    public sealed record Clip : IDisposable
    {
        public int Version;
        public Tags Tags;
        public int Rate;
        public float Overlap;
        public int Samples;
        public int Channels;
        public int Index;
        public int Count;
        public int Offset;
        public int Size;
        public int Generation;
        public byte[] Data = Array.Empty<byte>();

        public float Duration => (float)Samples / Rate;

        public void Dispose() => Pool<byte>.Put(ref Data);
    }

    [Serializable]
    public sealed record Icon : IDisposable
    {
        public int Version;
        public Tags Tags;
        public int Rate;
        public int Samples;
        public int Channels;
        public int Offset;
        public int Size;
        public int Generation;
        public string Description = "";
        public byte[] Data = Array.Empty<byte>();

        public void Dispose() => Pool<byte>.Put(ref Data);
    }

    public static (Process process, MemoryMappedFile memory) Create() => (Utility.Docker("audiocraft"), Utility.Memory("sound"));

    public static bool Load(int rate, int samples, int channels, byte[] data, ref AudioClip? audio)
    {
        if (audio == null || audio.samples != samples || audio.channels != channels)
            audio = AudioClip.Create("", samples, channels, rate, false);

        unsafe
        {
            fixed (byte* pointer = data)
            {
                return audio.SetData(new ReadOnlySpan<float>(pointer, data.Length / sizeof(float)), 0);
            }
        }
    }

    public static bool Load(Clip clip, ref AudioClip? audio) =>
        Load(clip.Rate, clip.Samples, clip.Channels, clip.Data, ref audio);

    public static bool TryLoad(Icon icon, Arrow arrow)
    {
        var audio = arrow.Audio;
        if (icon.Tags.HasFlag(arrow.Tags) && Load(icon.Rate, icon.Samples, icon.Channels, icon.Data, ref audio))
        {
            arrow.Audio = audio;
            arrow.Sound.clip = arrow.Audio;
            arrow.Icons.sound = icon;
            return true;
        }
        else return false;
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

    public static void Log(string message) => Debug.Log($"AUDIOCRAFT: {message.Truncate(2500)}");
    public static void Warn(string message) => Debug.LogWarning($"AUDIOCRAFT: {message.Truncate(2500)}");
    public static void Error(string message) => Debug.LogError($"AUDIOCRAFT: {message.Truncate(2500)}");
}