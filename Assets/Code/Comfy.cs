#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public static class Comfy
{
    [Serializable]
    public sealed record State
    {
        static int _version = 0;

        public static int Reserve() => Interlocked.Increment(ref _version);

        public static State? Sequence(IEnumerable<State> states)
        {
            var first = default(State);
            var previous = default(State);
            foreach (var current in states)
            {
                if (first is null || previous is null) (first, previous) = (current, current);
                else { previous.Next = current; previous = current; }
            }
            return first;
        }
        public static State? Sequence(params State[] states) => Sequence(states.AsEnumerable());
        public static State Sequence(State state, params Func<State, State>[] maps) => Sequence(maps.Select(map => map(state)))!;

        public int Version;
        public bool Loop;
        public Tags Tags;
        public int Width;
        public int Height;
        public int Offset;
        public int Size;
        public int Generation;
        public (int width, int height) Shape;
        public int Left;
        public int Right;
        public int Bottom;
        public int Top;
        public int Zoom;
        public int Steps;
        public float Guidance;
        public float Denoise;
        public bool Full;
        public int[] Cancel = Array.Empty<int>();
        public int[] Pause = Array.Empty<int>();
        public int[] Resume = Array.Empty<int>();
        public string Load = "";
        public string Data = "";
        public bool Empty;
        public string Positive = "";
        public string Negative = "";
        public State? Next;

        public State Map(Func<State, State> map) => map(Next is { } next ? this with { Next = next.Map(map) } : this);

        public override string ToString() => $@"{{
""version"":{Version},
""loop"":{(Loop ? "True" : "False")},
""tags"":{(int)Tags},
""width"":{Width},
""height"":{Height},
""offset"":{Offset},
""size"":{Size},
""generation"":{Generation},
""shape"":({Shape.height}, {Shape.width}),
""left"":{Left},
""right"":{Right},
""bottom"":{Bottom},
""top"":{Top},
""zoom"":{Zoom},
""steps"":{Steps},
""guidance"":{Guidance},
""denoise"":{Denoise},
""full"":{(Full ? "True" : "False")},
""cancel"":[{string.Join(",", Cancel)}],
""pause"":[{string.Join(",", Pause)}],
""resume"":[{string.Join(",", Resume)}],
""empty"":{(Empty ? "True" : "False")},
""positive"":""{Positive.Escape()}"",
""negative"":""{Negative.Escape()}"",
""data"":""{Data}"",
""load"":""{Load}"",
""next"":{Next?.ToString() ?? "None"}
}}".Replace("\n", "").Replace("\r", "");
    }

    [Serializable]
    public record Frame : IDisposable
    {
        public int Version;
        public Tags Tags;
        public int Generation;
        public int Index;
        public int Count;
        public int Width;
        public int Height;
        public int Offset;
        public int Size;
        public byte[] Data = Array.Empty<byte>();

        public void Dispose() => Pool<byte>.Put(ref Data);
    }

    [Serializable]
    public record Icon : IDisposable
    {
        public int Version;
        public Tags Tags;
        public int Generation;
        public int Width;
        public int Height;
        public int Offset;
        public int Size;
        public string Description = "";
        public byte[] Data = Array.Empty<byte>();

        public void Dispose() => Pool<byte>.Put(ref Data);
    }

    public static (Process process, MemoryMappedFile memory) Create() => (Utility.Docker("comfy"), Utility.Memory("image"));

    public unsafe static void Load(int width, int height, int offset, MemoryMappedFile memory, ref Texture2D? texture)
    {
        if (texture == null || texture.width != width || texture.height != height)
            texture = new Texture2D(width, height, TextureFormat.RGB24, 1, true, true);

        using var access = memory.CreateViewAccessor();
        try
        {
            var pointer = (byte*)IntPtr.Zero;
            access.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            if (pointer == null) throw new NullReferenceException();
            texture.LoadRawTextureData((IntPtr)(pointer + offset), width * height * 3);
        }
        finally { access.SafeMemoryMappedViewHandle.ReleasePointer(); }
        texture.Apply();
    }

    public static void Load(int width, int height, int offset, MemoryMappedFile memory, Image image, ref Texture2D? texture)
    {
        Load(width, height, offset, memory, ref texture);
        var area = new Rect(0f, 0f, width, height);
        var sprite = Sprite.Create(texture, area, area.center);
        image.sprite = sprite;
    }

    public static void Load(Frame frame, MemoryMappedFile memory, Image image, ref Texture2D? texture) =>
        Load(frame.Width, frame.Height, frame.Offset, memory, image, ref texture);

    public static bool TryLoad(Icon icon, MemoryMappedFile memory, Arrow arrow)
    {
        if (icon.Tags.HasFlag(arrow.Tags))
        {
            var texture = arrow.Texture;
            Load(icon.Width, icon.Height, icon.Offset, memory, arrow.Image, ref texture);
            arrow.Texture = texture;
            Utility.Set(ref arrow.Icons.image, icon);
            return true;
        }
        else
            return false;
    }

    public static void Load(int width, int height, byte[] data, ref Texture2D? texture)
    {
        if (texture == null || texture.width != width || texture.height != height)
            texture = new Texture2D(width, height, TextureFormat.RGB24, 1, true, true);

        texture.LoadRawTextureData(data);
        texture.Apply();
    }

    public static void Load(int width, int height, byte[] data, Image image, ref Texture2D? texture)
    {
        Load(width, height, data, ref texture);
        var area = new Rect(0f, 0f, width, height);
        var sprite = Sprite.Create(texture, area, area.center);
        image.sprite = sprite;
    }

    public static void Load(Frame frame, Image image, ref Texture2D? texture) =>
        Load(frame.Width, frame.Height, frame.Data, image, ref texture);

    public static bool TryLoad(Icon icon, Arrow arrow)
    {
        if (icon.Tags.HasFlag(arrow.Tags))
        {
            var texture = arrow.Texture;
            Load(icon.Width, icon.Height, icon.Data, arrow.Image, ref texture);
            arrow.Texture = texture;
            Utility.Set(ref arrow.Icons.image, icon);
            return true;
        }
        else
            return false;
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

    public static void Log(string message) => Debug.Log($"COMFY: {message.Truncate(2500)}");
    public static void Warn(string message) => Debug.LogWarning($"COMFY: {message.Truncate(2500)}");
    public static void Error(string message) => Debug.LogError($"COMFY: {message.Truncate(2500)}");
}