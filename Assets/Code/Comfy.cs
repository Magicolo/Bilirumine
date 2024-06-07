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
    public record Frame
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
    }

    [Serializable]
    public record Icon
    {
        public int Version;
        public Tags Tags;
        public int Generation;
        public int Width;
        public int Height;
        public int Offset;
        public int Size;
        public string Description = "";
    }

    public static (Process process, MemoryMappedFile memory) Create() => (Utility.Docker("comfy"), Utility.Memory("image"));

    public static bool Load(this MemoryMappedFile memory, int width, int height, int offset, int size, ref Texture2D? texture)
    {
        if (texture == null || texture.width != width || texture.height != height)
            texture = new Texture2D(width, height, TextureFormat.RGB24, 1, true, true);

        if (memory.Read(offset, (texture, size), static (state, pointer) => state.texture.LoadRawTextureData(pointer, state.size)))
        {
            texture.Apply();
            return true;
        }
        else
        {
            Error("Failed to acquire pointer to shared memory.");
            return false;
        }
    }

    public static bool Load(this MemoryMappedFile memory, Image output, Frame frame, ref Texture2D? texture)
    {
        if (memory.Load(frame.Width, frame.Height, frame.Offset, frame.Size, ref texture))
        {
            var area = new Rect(0f, 0f, frame.Width, frame.Height);
            var sprite = Sprite.Create(texture, area, area.center);
            output.sprite = sprite;
            return true;
        }
        else
            return false;
    }

    public static bool Load(this MemoryMappedFile memory, Arrow arrow, Icon icon)
    {
        var texture = arrow.Texture;
        if (icon.Tags.HasFlag(arrow.Tags) && memory.Load(icon.Width, icon.Height, icon.Offset, icon.Size, ref texture))
        {
            var area = new Rect(0f, 0f, icon.Width, icon.Height);
            arrow.Texture = texture;
            arrow.Image.sprite = Sprite.Create(arrow.Texture, area, area.center);
            arrow.Icons.image = icon;
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