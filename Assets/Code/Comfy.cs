#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

public sealed class Comfy
{
    [Serializable]
    sealed record Request
    {
        static int _version = 0;

        public static int Reserve() => Interlocked.Increment(ref _version);

        public static Request? Sequence(IEnumerable<Request> requests)
        {
            var first = default(Request);
            var previous = default(Request);
            foreach (var current in requests)
            {
                if (first is null || previous is null) (first, previous) = (current, current);
                else { previous.Next = current; previous = current; }
            }
            return first;
        }
        public static Request? Sequence(params Request[] requests) => Sequence(requests.AsEnumerable());
        public static Request Sequence(Request request, params Request[] requests) => Sequence(requests.AsEnumerable().Prepend(request))!;

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
        public string Description = "";
        public Request? Next;
        public Request Last => Next is { } next ? next.Last : this;

        public override string ToString() => $@"{{
""version"":{Version},
""loop"":{(Loop ? "True" : "False")},
""tags"":{(int)Tags},
""description"":""{Description.Escape()}"",
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
    sealed record Response
    {
        public int version;
        public int tags;
        public bool loop;
        public string description = "";
        public int width;
        public int height;
        public int count;
        public int offset;
        public int size;
        public int generation;
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

    static readonly ((int width, int height) low, (int width, int height) high) _resolutions = ((768, 512), (1024, 768));
    static readonly Request _frame = new()
    {
        Tags = Tags.Frame,
        Width = _resolutions.high.width,
        Height = _resolutions.high.height,
        Loop = true,
        Zoom = 64,
        Steps = 6,
        Guidance = 5f,
        Denoise = 0.7f,
        Full = true,
    };

    public static Comfy Create() => new(Utility.Docker("comfy"), Utility.Memory("image"));

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

    // public static void Load(Frame frame, Image image, ref Texture2D? texture)
    // {
    //     Load(frame.Width, frame.Height, frame.Data, image, ref texture);
    //     Pool<byte>.Put(ref frame.Data);
    // }

    // public static bool TryLoad(Icon icon, Arrow arrow)
    // {
    //     if (icon.Tags.HasFlag(arrow.Tags))
    //     {
    //         var texture = arrow.Texture;
    //         Load(icon.Width, icon.Height, icon.Data, arrow.Image, ref texture);
    //         arrow.Texture = texture;
    //         arrow.Icons.image = icon;
    //         Pool<byte>.Put(ref icon.Data);
    //         return true;
    //     }
    //     else
    //         return false;
    // }

    public static void Log(string message) => Utility.Log(nameof(Comfy), message);
    public static void Warn(string message) => Utility.Warn(nameof(Comfy), message);
    public static void Error(string message) => Utility.Error(nameof(Comfy), message);
    public static void Except(Exception exception) => Utility.Except(nameof(Comfy), exception);


    public float Images => 1f / _delta.image;
    public float Batches => 1f / _delta.batch;
    public int Frames => _frames.Count;
    public float Rate => _delta.speed / _delta.wait;
    public float Wait => _delta.wait;
    public float Speed => _delta.speed;
    public (float width, float height) Resolution => _resolution;

    Process _process;
    int _begin;
    int _end;
    bool _play;
    (int width, int height) _resolution;
    Frame _last = new();
    (float image, float batch, float wait, float speed) _delta = (0.1f, 5f, 0.1f, 1f);
    readonly (ConcurrentQueue<TimeSpan> images, ConcurrentQueue<TimeSpan> batches) _deltas = (
        new(Enumerable.Range(0, 250).Select(_ => TimeSpan.FromSeconds(0.1f))),
        new(Enumerable.Range(0, 5).Select(_ => TimeSpan.FromSeconds(5f))));
    readonly MemoryMappedFile _memory;
    readonly object _lock = new();
    readonly ConcurrentQueue<Frame> _frames = new();
    readonly ConcurrentQueue<Icon> _icons = new();
    readonly HashSet<int> _cancel = new();
    readonly ConcurrentDictionary<(Tags tags, bool loop), Request> _requests = new();

    Comfy(Process process, MemoryMappedFile memory)
    {
        _process = process;
        _memory = memory;
    }

    public void Set(bool? play = null) => _play = play ?? _play;
    public bool Has(int? begin = null, int? end = null) => _begin >= begin || _end >= end;

    public IEnumerator UpdateFrames(Image image)
    {
        var time = Time.time;
        var main = default(Texture2D);
        var load = default(Texture2D);
        foreach (var item in Loop())
        {
            if (_play && _frames.TryDequeue(out var frame))
            {
                if (_cancel.Contains(frame.Version)) continue;
                else _last = frame;

                while (Time.time - time < _delta.wait / _delta.speed) yield return null;
                time += _delta.wait / _delta.speed;
                Load(frame, _memory, image, ref load);
                (main, load) = (load, main);
                _resolution = (frame.Width, frame.Height);
            }
            else time = Time.time;
            yield return item;
        }
    }

    public IEnumerator UpdateIcons(Arrow[] arrows)
    {
        foreach (var item in Loop())
        {
            if (_icons.TryDequeue(out var icon))
            {
                foreach (var arrow in arrows)
                {
                    if (icon.Tags.HasFlag(arrow.Tags))
                    {
                        var texture = arrow.Texture;
                        Load(icon.Width, icon.Height, icon.Offset, _memory, arrow.Image, ref texture);
                        arrow.Texture = texture;
                        arrow.Icons.image = icon;
                    }
                }
            }
            yield return item;
        }
    }

    public IEnumerator UpdateDelta()
    {
        foreach (var item in Loop())
        {
            _delta.image = _deltas.images.Select(delta => (float)delta.TotalSeconds).Average();
            _delta.batch = _deltas.batches.Select(delta => (float)delta.TotalMinutes).Average();
            _delta.wait = Mathf.Lerp(_delta.wait, _delta.image, Time.deltaTime);

            var rate = 10f / _delta.wait;
            var ratio = Mathf.Pow(Mathf.Clamp01(_frames.Count / rate), 2f);
            _delta.speed = Mathf.Lerp(_delta.speed, 0.5f + ratio, Time.deltaTime * 0.25f);
            yield return item;
        }
    }

    public async Task Read()
    {
        var watch = Stopwatch.StartNew();
        var then = (image: watch.Elapsed, batch: watch.Elapsed);
        foreach (var _ in Loop())
        {
            var line = await _process.StandardOutput.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var response = JsonUtility.FromJson<Response>(line);
                Log($"Received output: {response}");
                var tags = (Tags)response.tags;
                var offset = response.offset;
                var size = response.size / response.count;
                _requests.TryRemove((tags, false), out var __);
                if (tags.HasFlag(Tags.Begin)) _begin = response.version;
                if (tags.HasFlag(Tags.End)) _end = response.version;
                if (tags.HasFlag(Tags.Frame))
                {
                    var frame = new Frame
                    {
                        Version = response.version,
                        Generation = response.generation,
                        Count = response.count,
                        Width = response.width,
                        Height = response.height,
                        Size = size,
                        Tags = tags,
                    };
                    for (int i = 0; i < response.count; i++, offset += size)
                    {
                        _frames.Enqueue(frame with { Index = i, Offset = offset });
                        var now = watch.Elapsed;
                        if (_deltas.images.TryDequeue(out var ___)) _deltas.images.Enqueue(now - then.image);
                        then.image = now;
                    }
                    {
                        var now = watch.Elapsed;
                        if (_deltas.batches.TryDequeue(out var ___)) _deltas.batches.Enqueue(now - then.batch);
                        then.batch = now;
                    }
                }
                if (tags.HasFlag(Tags.Icon))
                {
                    _icons.Enqueue(new()
                    {
                        Version = response.version,
                        Tags = tags,
                        Offset = offset,
                        Size = size,
                        Generation = response.generation,
                        Width = response.width,
                        Height = response.height,
                        Description = response.description,
                    });
                }
            }
            catch (ArgumentException) { Warn(line); }
            catch (FormatException) { Warn(line); }
            catch (Exception exception) { Except(exception); }
        }
    }

    public void WriteFrames(string positive, string negative, int? width, int? height, string? data)
    {
        var cancel = 0;
        if (_requests.TryRemove((_frame.Tags, true), out var request)) _cancel.Add(cancel = request.Version);

        Write(_frame with
        {
            Version = Request.Reserve(),
            Data = data ?? "",
            Empty = true,
            Shape = (width ?? 0, height ?? 0),
            Positive = positive,
            Negative = negative,
            Cancel = new[] { cancel },
        }, true);
    }

    public int WriteBegin(Arrow arrow, (string from, string to) positives, string negative)
    {
        var pause = 0;
        if (_requests.TryGetValue((_frame.Tags, true), out var request)) pause = request.Version;

        var version = Request.Reserve();
        var template = _frame with
        {
            Version = version,
            Tags = _frame.Tags,
            Loop = false,
            Offset = _last.Offset,
            Size = _last.Size,
            Generation = _last.Generation,
            Shape = (_last.Width, _last.Height),
            Width = _resolutions.low.width,
            Height = _resolutions.low.height,
            Steps = 5,
            Guidance = 6f,
            Denoise = 0.4f,
            Negative = negative,
            Pause = new[] { pause },
            Left = Math.Max(-arrow.Direction.x, 0),
            Right = Math.Max(arrow.Direction.x, 0),
            Top = Math.Max(arrow.Direction.y, 0),
            Bottom = Math.Max(-arrow.Direction.y, 0),
        };
        Write(Request.Sequence(
            template with { Tags = template.Tags | Tags.Begin, Positive = positives.from },
            template with { Tags = template.Tags, Positive = $"{positives.from} {positives.to}" },
            template with { Tags = template.Tags, Positive = $"{positives.to} {positives.from}" },
            template with { Tags = template.Tags | Tags.End, Positive = positives.to },
            _frame with { Version = Request.Reserve(), Positive = positives.to, Negative = negative }
        ), true);
        return version;
    }

    public void WriteEnd()
    {
        var cancel = 0;
        if (_requests.TryRemove((_frame.Tags, true), out var request))
            _cancel.Add(cancel = request.Version);
        if (_requests.TryRemove((_frame.Tags | Tags.Begin, true), out request) && request.Last with { Empty = true } is { } last)
            _requests.AddOrUpdate((last.Tags, true), last, (_, _) => last);
        Write(new() { Version = Request.Reserve(), Cancel = new[] { cancel } }, false);
    }

    public void WriteCancel(int version)
    {
        var resume = 0;
        if (_requests.TryGetValue((_frame.Tags, true), out var request)) resume = request.Version;
        _cancel.Add(version);
        Write(new() { Version = Request.Reserve(), Resume = new[] { resume }, Cancel = new[] { version } }, false);
    }

    public async Task WriteIcon(Arrow arrow, int version, string positive, string negative)
    {
        for (int i = 0; i < 256 && _end < version; i++) await Task.Delay(100);
        Write(new()
        {
            Version = Request.Reserve(),
            Tags = Tags.Icon | arrow.Tags,
            Load = $"{arrow.Color}.png".ToLowerInvariant(),
            Positive = $"{Utility.Styles("icon", "close up", "huge", "simple", "minimalistic", "figurative")} ({arrow.Color}) {positive}",
            Negative = negative,
            Width = 512,
            Height = 512,
            Steps = 5,
            Guidance = 6f,
            Denoise = 0.7f,
            Description = positive,
        }, true);
    }

    void Write(Request request, bool store)
    {
        foreach (var _ in Loop())
        {
            if (store) _requests.AddOrUpdate((request.Tags, request.Last.Loop), request, (_, _) => request);
            try
            {
                Log($"Sending input '{request}'.");
                _process.StandardInput.WriteLine($"{request}");
                _process.StandardInput.Flush();
                break;
            }
            catch (Exception exception) { Except(exception); }
        }
    }

    IEnumerable Loop()
    {
        while (true)
        {
            if (_process.HasExited)
            {
                lock (_lock)
                {
                    if (_process.HasExited)
                    {
                        Warn("Restarting docker container.");
                        _process = Utility.Docker("comfy");
                        foreach (var pair in _requests.OrderBy(pair => pair.Value.Version)) Write(pair.Value, true);
                    }
                }
            }
            yield return null;
        }
    }
}