#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public sealed class Audiocraft
{
    [Serializable]
    sealed record Request
    {
        static int _version = 0;

        public static int Reserve() => Interlocked.Increment(ref _version);

        public int Version;
        public Tags Tags;
        public string Description = "";
        public bool Loop;
        public bool Empty;
        public int Offset;
        public int Size;
        public int Generation;
        public float Duration;
        public float Overlap;
        public string[] Prompts = Array.Empty<string>();
        public int[] Cancel = Array.Empty<int>();
        public int[] Pause = Array.Empty<int>();
        public int[] Resume = Array.Empty<int>();
        public string Data = "";

        public override string ToString() => $@"{{
""version"":{Version},
""tags"":{(int)Tags},
""description"":""{Description.Escape()}"",
""loop"":{(Loop ? "True" : "False")},
""empty"":{(Empty ? "True" : "False")},
""offset"":{Offset},
""size"":{Size},
""generation"":{Generation},
""duration"":{Duration},
""overlap"":{Overlap},
""prompts"":[{string.Join(",", Prompts.Select(prompt => $@"""{prompt.Escape()}"""))}],
""cancel"":[{string.Join(",", Cancel)}],
""pause"":[{string.Join(",", Pause)}],
""resume"":[{string.Join(",", Resume)}],
""data"":""{Data}"",
}}".Replace("\n", "").Replace("\r", "");
    }

    [Serializable]
    sealed record Response
    {
        public int version;
        public int tags;
        public bool loop;
        public string description = "";
        public float overlap;
        public int rate;
        public int samples;
        public int channels;
        public int count;
        public int offset;
        public int size;
        public int generation;
    }

    [Serializable]
    public sealed record Clip
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
    }

    [Serializable]
    public sealed record Icon
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
        public float Duration => (float)Samples / Rate;
    }

    public static Audiocraft Create() => new(Utility.Docker("audiocraft"), Utility.Memory("sound"));

    public unsafe static void Load(int rate, int samples, int channels, int offset, MemoryMappedFile memory, ref AudioClip? audio)
    {
        if (audio == null || audio.samples != samples || audio.channels != channels)
            audio = AudioClip.Create("", samples, channels, rate, false);

        using var access = memory.CreateViewAccessor();
        try
        {
            var pointer = (byte*)IntPtr.Zero;
            access.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            if (pointer == null) throw new NullReferenceException();
            audio.SetData(new ReadOnlySpan<float>(pointer + offset, samples * channels), 0);
        }
        finally { access.SafeMemoryMappedViewHandle.ReleasePointer(); }
    }

    public static bool Load(int rate, int samples, int channels, byte[] data, ref AudioClip? audio)
    {
        if (audio == null || audio.samples != samples || audio.channels != channels)
            audio = AudioClip.Create("", samples, channels, rate, false);

        return audio.SetData(MemoryMarshal.Cast<byte, float>(data), 0);
    }

    public static void Log(string message) => Utility.Log(nameof(Audiocraft), message);
    public static void Warn(string message) => Utility.Warn(nameof(Audiocraft), message);
    public static void Error(string message) => Utility.Error(nameof(Audiocraft), message);
    public static void Except(Exception exception) => Utility.Except(nameof(Audiocraft), exception);

    Process _process;
    float _volume = 1f;
    float _motion = 1f;
    float _pitch = 1f;
    bool _pause;
    readonly MemoryMappedFile _memory;
    readonly ConcurrentQueue<Clip> _clips = new();
    readonly ConcurrentQueue<Icon> _icons = new();
    readonly ConcurrentDictionary<(Tags tags, bool loop), Request> _requests = new();
    readonly HashSet<int> _cancel = new();
    readonly object _lock = new();

    public int Clips => _clips.Count;
    float Volume => Mathf.Clamp01(_volume * _motion);

    Audiocraft(Process process, MemoryMappedFile memory)
    {
        _process = process;
        _memory = memory;
    }

    public void Set(float? volume = null, float? motion = null, float? pitch = null, float time = 1f)
    {
        _volume = Mathf.Lerp(_volume, volume ?? _volume, time);
        _motion = Mathf.Lerp(_motion, motion ?? _motion, time);
        _pitch = Mathf.Lerp(_pitch, pitch ?? _pitch, time);
    }

    public IEnumerator UpdateClips(AudioSource @in, AudioSource @out)
    {
        var main = default(AudioClip);
        var load = default(AudioClip);
        foreach (var item in Loop())
        {
            if (_clips.TryDequeue(out var clip))
            {
                if (_cancel.Contains(clip.Version)) continue;

                Load(clip, ref load);
                @in.clip = load;
                @in.volume = 0f;
                (main, load) = (load, main);
                var fade = clip.Overlap * clip.Duration;
                while (!Utility.Chain(@out, @in, Volume, _pitch, fade)) yield return null;
                (@in, @out) = (@out, @in);
            }
            else
            {
                @in.volume = Mathf.Lerp(@in.volume, 0, Time.deltaTime);
                @in.pitch = Mathf.Lerp(@in.pitch, _pitch, Time.deltaTime);
                @out.volume = Mathf.Lerp(@out.volume, Volume, Time.deltaTime);
                @out.pitch = Mathf.Lerp(@out.pitch, _pitch, Time.deltaTime);
            }
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
                        var audio = arrow.Audio;
                        Load(icon, ref audio);
                        arrow.Audio = audio;
                        arrow.Sound.clip = arrow.Audio;
                        arrow.Icons.sound = icon;
                    }
                }
            }
            yield return item;
        }
    }

    public IEnumerator UpdatePause()
    {
        foreach (var item in Loop())
        {
            if (_requests.TryGetValue((Tags.Clip, true), out var request))
            {
                switch (_pause, _clips.Count(clip => clip.Version == request.Version))
                {
                    case (false, > 5):
                        Write(new() { Version = Request.Reserve(), Pause = new[] { request.Version } }, false);
                        _pause = true;
                        break;
                    case (true, < 5):
                        Write(new() { Version = Request.Reserve(), Resume = new[] { request.Version } }, false);
                        _pause = false;
                        break;
                }
            }
            yield return item;
        }
    }

    public void WriteClips(string prompt, Icon? icon, string? data)
    {
        var cancel = 0;
        if (_requests.TryRemove((Tags.Clip, true), out var request)) _cancel.Add(cancel = request.Version);

        Write(new()
        {
            Version = Request.Reserve(),
            Tags = Tags.Clip,
            Loop = true,
            Duration = 10f,
            Overlap = 0.5f,
            Offset = icon?.Offset ?? 0,
            Size = icon?.Size ?? 0,
            Generation = icon?.Generation ?? 0,
            Empty = true, // If the sound could not be loaded (ex: overwritten), allow generation from scratch.
            Cancel = new[] { cancel },
            Prompts = new[] { prompt },
            Data = data ?? "",
        }, true);
    }

    public async Task WriteIcon(Arrow arrow, string description)
    {
        for (int i = 0; i < 256 && _clips.Count < 5; i++) await Task.Delay(100);
        var version = Request.Reserve();
        var tags = Tags.Icon | arrow.Tags;
        var prompt = $"{Utility.Styles("punchy", "jingle", "stinger", "notification")} ({arrow.Color}) {description}";
        Write(new()
        {
            Version = version,
            Tags = tags,
            Duration = 10f,
            Empty = true,
            Prompts = new[] { prompt },
            Description = description,
        }, true);
    }

    public async Task Read()
    {
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
                if (tags.HasFlag(Tags.Clip))
                {
                    var clip = new Clip
                    {
                        Version = response.version,
                        Tags = tags,
                        Size = size,
                        Rate = response.rate,
                        Overlap = response.overlap,
                        Samples = response.samples,
                        Channels = response.channels,
                        Count = response.count,
                        Generation = response.generation,
                    };
                    for (int i = 0; i < response.count; i++, offset += size)
                        _clips.Enqueue(clip with
                        {
                            Index = i,
                            Offset = offset,
                            Data = await _memory.Read(offset, size),
                        });
                }
                if (tags.HasFlag(Tags.Icon))
                {
                    _icons.Enqueue(new()
                    {
                        Version = response.version,
                        Tags = tags,
                        Offset = offset,
                        Size = size,
                        Rate = response.rate,
                        Samples = response.samples,
                        Channels = response.channels,
                        Generation = response.generation,
                        Description = response.description,
                        Data = await _memory.Read(offset, size),
                    });
                }
            }
            catch (ArgumentException) { Warn(line); }
            catch (FormatException) { Warn(line); }
            catch (Exception exception) { Except(exception); }
        }
    }

    void Write(Request request, bool store)
    {
        foreach (var _ in Loop())
        {
            if (store) _requests.AddOrUpdate((request.Tags, request.Loop), request, (_, _) => request);
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

    void Load(Clip clip, ref AudioClip? audio)
    {
        if (clip.Data.Length > 0)
            Load(clip.Rate, clip.Samples, clip.Channels, clip.Data, ref audio);
        else
            Load(clip.Rate, clip.Samples, clip.Channels, clip.Offset, _memory, ref audio);
        Pool<byte>.Put(ref clip.Data);
    }

    void Load(Icon icon, ref AudioClip? audio)
    {
        if (icon.Data.Length > 0)
            Load(icon.Rate, icon.Samples, icon.Channels, icon.Data, ref audio);
        else
            Load(icon.Rate, icon.Samples, icon.Channels, icon.Offset, _memory, ref audio);
        Pool<byte>.Put(ref icon.Data);
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
                        _process = Utility.Docker("audiocraft");
                        foreach (var pair in _requests.OrderBy(pair => pair.Value.Version)) Write(pair.Value, true);
                    }
                }
            }
            yield return null;
        }
    }
}