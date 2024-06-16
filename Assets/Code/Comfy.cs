#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

        public static Request Sequence(Request request, params Request[] requests)
        {
            var current = request;
            foreach (var next in requests)
            {
                current.Next = next;
                current = next;
            }
            return request;
        }

        public int Version;
        public bool Loop;
        public Tags Tags;
        public int Width;
        public int Height;
        public (int width, int height) Shape;
        public int Left;
        public int Right;
        public int Bottom;
        public int Top;
        public int Zoom;
        public int Steps;
        public (float scale, int multiplier)[] Interpolations = Array.Empty<(float, int)>();
        public float Guidance;
        public float Denoise;
        public bool Continue;
        public int[] Cancel = Array.Empty<int>();
        public int[] Pause = Array.Empty<int>();
        public int[] Resume = Array.Empty<int>();
        public string Load = "";
        public bool Empty;
        public string Positive = "";
        public string Negative = "";
        public string Description = "";
        public byte[] Data = Array.Empty<byte>();
        public Request? Next;
        public Request Last => Next is { } next ? next.Last : this;

        public override string ToString() => $@"{{
""version"":{Version},
""loop"":{(Loop ? "True" : "False")},
""tags"":{(int)Tags},
""description"":""{Description.Escape()}"",
""width"":{Width},
""height"":{Height},
""shape"":({Shape.height}, {Shape.width}),
""left"":{Left},
""right"":{Right},
""bottom"":{Bottom},
""top"":{Top},
""zoom"":{Zoom},
""steps"":{Steps},
""guidance"":{Guidance},
""denoise"":{Denoise},
""interpolations"":[{string.Join(", ", Interpolations.Select(pair => $"({pair.scale},{pair.multiplier})"))}],
""cancel"":[{string.Join(",", Cancel)}],
""pause"":[{string.Join(",", Pause)}],
""resume"":[{string.Join(",", Resume)}],
""empty"":{(Empty ? "True" : "False")},
""positive"":""{Positive.Escape()}"",
""negative"":""{Negative.Escape()}"",
""load"":""{Load}"",
""data"":""{Convert.ToBase64String(Data)}"",
""next"":{Next?.ToString() ?? "None"},
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
        public int Index;
        public int Count;
        public int Width;
        public int Height;
        public byte[] Data = Array.Empty<byte>();
    }

    [Serializable]
    public record Icon
    {
        public int Version;
        public Tags Tags;
        public int Width;
        public int Height;
        public string Description = "";
        public byte[] Data = Array.Empty<byte>();
    }

    static readonly string _negative = string.Join(", ", "low detail", "plain", "simple", "sparse", "blurry", "worst quality", "nude", "nudity", "naked", "sexy", "sexual", "genital", "child", "children", "teenager", "woman");
    static readonly Request _frame = new()
    {
        Tags = Tags.Frame,
        Width = 1024,
        Height = 768,
        Steps = 6,
        Guidance = 5f,
        Denoise = 0.7f,
        Zoom = 64,
        Loop = true,
        Interpolations = new[] { (0.25f, 10), (1f, 10) },
        Negative = _negative,
    };
    static readonly Request _move = new()
    {
        Tags = Tags.Frame | Tags.Move,
        Width = 768,
        Height = 512,
        Steps = 5,
        Guidance = 6f,
        Denoise = 0.4f,
        Continue = true,
        Interpolations = new[] { (0.25f, 10), (0.5f, 10) },
        Negative = _negative,
    };
    static readonly Request _icon = new()
    {
        Tags = Tags.Icon,
        Width = 512,
        Height = 512,
        Steps = 5,
        Guidance = 6f,
        Denoise = 0.7f,
        Negative = _negative,
    };

    public unsafe static void Load(int width, int height, int offset, int size, Memory memory, ref Texture2D? texture)
    {
        if (texture == null || texture.width != width || texture.height != height)
            texture = new Texture2D(width, height, TextureFormat.RGB24, 1, true, true);

        memory.Load(offset, size, texture);
    }

    public static void Load(int width, int height, byte[] data, ref Texture2D? texture)
    {
        if (texture == null || texture.width != width || texture.height != height)
            texture = new Texture2D(width, height, TextureFormat.RGB24, 1, true, true);

        texture.LoadRawTextureData(data);
        texture.Apply();
    }

    public static void Load(int width, int height, int offset, int size, Memory memory, Image image, ref Texture2D? texture)
    {
        Load(width, height, offset, size, memory, ref texture);
        var area = new Rect(0f, 0f, width, height);
        image.sprite = Sprite.Create(texture, area, area.center);
    }

    public static void Load(int width, int height, byte[] data, Image image, ref Texture2D? texture)
    {
        Load(width, height, data, ref texture);
        var area = new Rect(0f, 0f, width, height);
        image.sprite = Sprite.Create(texture, area, area.center);
    }

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

    Process _process = Utility.Docker("comfy");
    int _begin;
    int _end;
    bool _play;
    (int width, int height) _resolution;
    Frame _last = new();
    (float image, float batch, float wait, float speed) _delta = (0.1f, 5f, 0.1f, 1f);
    readonly (ConcurrentQueue<TimeSpan> images, ConcurrentQueue<TimeSpan> batches) _deltas = (
        new(Enumerable.Range(0, 250).Select(_ => TimeSpan.FromSeconds(0.1f))),
        new(Enumerable.Range(0, 5).Select(_ => TimeSpan.FromSeconds(5f))));
    readonly Memory _memory = Utility.Memory("image");
    readonly object _lock = new();
    readonly ConcurrentQueue<Frame> _frames = new();
    readonly ConcurrentQueue<Icon> _icons = new();
    readonly HashSet<int> _cancel = new();
    readonly ConcurrentDictionary<(Tags tags, bool loop), Request> _requests = new();

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
                else
                {
                    var last = _last;
                    _last = frame;
                    Pool<byte>.Put(ref last.Data);
                }

                while (Time.time - time < _delta.wait / _delta.speed) yield return null;
                time += _delta.wait / _delta.speed;
                Load(frame.Width, frame.Height, frame.Data, image, ref load);
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
                        Load(icon.Width, icon.Height, icon.Data, arrow.Image, ref texture);
                        if (arrow.Icons.image is { } image) Pool<byte>.Put(ref image.Data);
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
                _requests.TryRemove((tags, false), out var __);
                if (tags.HasFlag(Tags.Begin)) _begin = response.version;
                if (tags.HasFlag(Tags.End)) _end = response.version;
                if (tags.HasFlag(Tags.Frame))
                {
                    var index = 0;
                    await foreach (var data in _memory.Read(response.offset, response.size, response.count))
                    {
                        _frames.Enqueue(new()
                        {
                            Version = response.version,
                            Tags = tags,
                            Count = response.count,
                            Width = response.width,
                            Height = response.height,
                            Index = index++,
                            Data = data
                        });
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
                else if (tags.HasFlag(Tags.Icon))
                {
                    _icons.Enqueue(new()
                    {
                        Version = response.version,
                        Tags = tags,
                        Width = response.width,
                        Height = response.height,
                        Description = response.description,
                        Data = await _memory.Read(response.offset, response.size),
                    });
                }
            }
            catch (ArgumentException) { Warn(line); }
            catch (FormatException) { Warn(line); }
            catch (Exception exception) { Except(exception); }
        }
    }

    public async Task WriteFrames(string positive, int? width, int? height, byte[]? data)
    {
        var cancel = 0;
        if (_requests.TryRemove((_frame.Tags, true), out var request)) _cancel.Add(cancel = request.Version);

        await Write(_frame with
        {
            Version = Request.Reserve(),
            Data = data ?? Array.Empty<byte>(),
            Empty = true,
            Shape = (width ?? 0, height ?? 0),
            Positive = positive,
            Cancel = new[] { cancel },
        }, true);
    }

    public async Task<int> WriteBegin(Arrow arrow, (string from, string to) positives)
    {
        var pause = 0;
        if (_requests.TryGetValue((_frame.Tags, true), out var request)) pause = request.Version;

        var version = Request.Reserve();
        var template = _move with
        {
            Version = version,
            Pause = new[] { pause },
            Left = Math.Max(-arrow.Direction.x, 0),
            Right = Math.Max(arrow.Direction.x, 0),
            Top = Math.Max(arrow.Direction.y, 0),
            Bottom = Math.Max(-arrow.Direction.y, 0),
        };
        await Write(Request.Sequence(
            template with { Tags = template.Tags | Tags.Begin, Positive = positives.from },
            template with { Tags = template.Tags, Positive = $"{positives.from} {positives.to}" },
            template with { Tags = template.Tags, Positive = $"{positives.to} {positives.from}" },
            template with { Tags = template.Tags | Tags.End, Positive = positives.to },
            _frame with { Version = version, Positive = positives.to, Continue = true }
        ), true);
        return version;
    }

    public async Task WriteEnd()
    {
        var cancel = 0;
        if (_requests.TryRemove((_frame.Tags, true), out var request))
            _cancel.Add(cancel = request.Version);
        if (_requests.TryRemove((_move.Tags | Tags.Begin, true), out request) && request.Last with { Empty = true } is { } last)
            _requests.AddOrUpdate((last.Tags, true), last, (_, _) => last);
        await Write(new() { Version = Request.Reserve(), Cancel = new[] { cancel } }, false);
    }

    public async Task WriteCancel(Task<int> version)
    {
        var cancel = await version;
        var resume = 0;
        if (_requests.TryGetValue((_frame.Tags, true), out var request)) resume = request.Version;
        _cancel.Add(cancel);
        await Write(new() { Version = Request.Reserve(), Resume = new[] { resume }, Cancel = new[] { cancel } }, false);
    }

    public async Task WriteIcon(Arrow arrow, int version, string positive)
    {
        for (int i = 0; i < 256 && _end < version; i++) await Task.Delay(100);
        await Write(_icon with
        {
            Version = Request.Reserve(),
            Tags = _icon.Tags | arrow.Tags,
            Load = $"{arrow.Color}.png".ToLowerInvariant(),
            Positive = $"{Utility.Styles("icon", "close up", "huge", "simple", "minimalistic", "figurative")} ({arrow.Color}) {positive}",
            Description = positive,
        }, true);
    }

    async Task Write(Request request, bool store)
    {
        foreach (var _ in Loop()) if (await TryWrite(request, store)) break;
    }

    async Task<bool> TryWrite(Request request, bool store)
    {
        if (store) _requests.AddOrUpdate((request.Tags, request.Last.Loop), request, (_, _) => request);
        if (request.Continue && _last is { } last) request = request with
        {
            Shape = (last.Width, last.Height),
            Data = last.Data,
        };

        try
        {
            Log($"Sending input '{request}'.");
            await _process.StandardInput.WriteLineAsync($"{request}");
            await _process.StandardInput.FlushAsync();
            return true;
        }
        catch (Exception exception) { Except(exception); }
        return false;
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
                        foreach (var pair in _requests.OrderBy(pair => pair.Value.Version))
                            TryWrite(pair.Value, true).Wait();
                    }
                }
            }
            yield return null;
        }
    }
}