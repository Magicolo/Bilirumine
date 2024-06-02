#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

public static class Comfy
{
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

        static string? Escape(string? value) => value?.Replace(@"""", @"\""");

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
        public int[]? Cancel;
        public int[]? Pause;
        public int[]? Resume;
        public string? Load;
        public bool Empty;
        public string? Positive;
        public string? Negative;
        public State? Next;

        public State Map(Func<State, State> map) => map(Next is { } next ? this with { Next = next.Map(map) } : this);

        public override string ToString() => $@"{{""version"":{Version},""loop"":{(Loop ? "True" : "False")},""tags"":{(int)Tags},""width"":{Width},""height"":{Height},""offset"":{Offset},""size"":{Size},""generation"":{Generation},""shape"":({Shape.height}, {Shape.width}),""left"":{Left},""right"":{Right},""bottom"":{Bottom},""top"":{Top},""zoom"":{Zoom},""steps"":{Steps},""guidance"":{Guidance},""denoise"":{Denoise},""full"":{(Full ? "True" : "False")},""cancel"":[{string.Join(",", Cancel ?? Array.Empty<int>())}],""pause"":[{string.Join(",", Pause ?? Array.Empty<int>())}],""resume"":[{string.Join(",", Resume ?? Array.Empty<int>())}],""empty"":{(Empty ? "True" : "False")},""positive"":""{Escape(Positive)}"",""negative"":""{Escape(Negative)}"",""load"":""{Load}"",""next"":{Next?.ToString() ?? "None"}}}";
    }

    public record Frame
    {
        public int Version;
        public int Generation;
        public int Index;
        public int Count;
        public int Width;
        public int Height;
        public int Offset;
        public int Size;
        public Tags Tags;
    }

    public record Icon
    {
        public int Version;
        public int Generation;
        public int Width;
        public int Height;
        public int Offset;
        public int Size;
        public Tags Tags;
        public string Description = "";
    }

    [Flags]
    public enum Tags
    {
        Frame = 1 << 0,
        Icon = 1 << 1,
        Left = 1 << 2,
        Right = 1 << 3,
        Up = 1 << 4,
        Down = 1 << 5,
        Begin = 1 << 6,
        End = 1 << 7,
        Move = 1 << 8
    }
    public static (Process process, MemoryMappedFile memory) Create() => (Utility.Docker("comfy"), Utility.Memory("image"));

    public static bool Load(this MemoryMappedFile memory, int width, int height, int offset, int size, ref Texture2D? texture)
    {
        if (texture == null || texture.width != width || texture.height != height)
            texture = new Texture2D(width, height, TextureFormat.RGB24, 1, true, true);

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
                    texture.LoadRawTextureData((IntPtr)(pointer + offset), size);
                }
                finally { access.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
        }
        texture.Apply();
        return true;
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
            arrow.Icon = icon;
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

    public static void Log(string message) => Debug.Log($"COMFY: {message}");
    public static void Warn(string message) => Debug.LogWarning($"COMFY: {message}");
    public static void Error(string message) => Debug.LogError($"COMFY: {message}");

    public static string Styles(params string[] styles) => Styles(1f, styles);
    public static string Styles(float strength, params string[] styles) => Styles(styles.Select(style => (style, strength)));
    public static string Styles(params (string style, float strength)[] styles) => Styles(styles.AsEnumerable());
    public static string Styles(IEnumerable<(string style, float strength)> styles) => string.Join(" ", styles.Select(pair => pair.strength == 1f ? $"({pair.style})" : $"({pair.style}:{pair.strength})"));
}