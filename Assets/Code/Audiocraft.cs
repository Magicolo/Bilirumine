#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        public string Description = "";
        public byte[] Data = Array.Empty<byte>();
        public float Duration => (float)Samples / Rate;
    }

    public unsafe static bool Load(int rate, int samples, int channels, int offset, int size, Memory memory, ref AudioClip? audio)
    {
        if (audio == null || audio.samples != samples || audio.channels != channels)
            audio = AudioClip.Create("", samples, channels, rate, false);

        return memory.Load(offset, size, audio);
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

    Process _process = Utility.Docker("audiocraft");
    float _volume = 1f;
    float _motion = 1f;
    float _pitch = 1f;
    bool _pause;
    readonly Memory _memory = Utility.Memory("sound");
    readonly ConcurrentQueue<Clip> _clips = new();
    readonly ConcurrentQueue<Icon> _icons = new();
    readonly ConcurrentDictionary<(Tags tags, bool loop), Request> _requests = new();
    readonly HashSet<int> _cancel = new();
    readonly object _lock = new();

    public int Clips => _clips.Count;
    float Volume => Mathf.Clamp01(_volume * _motion);

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

    public void WriteClips(string prompt, string? data)
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
            Empty = true,
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
                _requests.TryRemove((tags, false), out var __);
                if (tags.HasFlag(Tags.Clip))
                {
                    var index = 0;
                    await foreach (var data in _memory.Read(response.offset, response.size, response.count))
                    {
                        _clips.Enqueue(new()
                        {
                            Version = response.version,
                            Tags = tags,
                            Rate = response.rate,
                            Overlap = response.overlap,
                            Samples = response.samples,
                            Channels = response.channels,
                            Count = response.count,
                            Index = index++,
                            Data = data
                        });
                    }
                }
                else if (tags.HasFlag(Tags.Icon))
                {
                    _icons.Enqueue(new()
                    {
                        Version = response.version,
                        Tags = tags,
                        Rate = response.rate,
                        Samples = response.samples,
                        Channels = response.channels,
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

    void Write(Request request, bool store)
    {
        foreach (var _ in Loop()) if (TryWrite(request, store)) break;
    }

    bool TryWrite(Request request, bool store)
    {
        if (store) _requests.AddOrUpdate((request.Tags, request.Loop), request, (_, _) => request);
        try
        {
            Log($"Sending input '{request}'.");
            _process.StandardInput.WriteLine($"{request}");
            _process.StandardInput.Flush();
            return true;
        }
        catch (Exception exception) { Except(exception); }
        return false;
    }

    void Load(Clip clip, ref AudioClip? audio)
    {
        Load(clip.Rate, clip.Samples, clip.Channels, clip.Data, ref audio);
        Pool<byte>.Put(ref clip.Data);
    }

    void Load(Icon icon, ref AudioClip? audio)
    {
        Load(icon.Rate, icon.Samples, icon.Channels, icon.Data, ref audio);
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
                        foreach (var pair in _requests.OrderBy(pair => pair.Value.Version)) TryWrite(pair.Value, true);
                    }
                }
            }
            yield return null;
        }
    }
}