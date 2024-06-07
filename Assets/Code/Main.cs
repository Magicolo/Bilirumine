#nullable enable

using System.Diagnostics;
using UnityEngine;
using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using System.Linq;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;
using Random = UnityEngine.Random;
using System.Collections.Generic;
using UnityEngine.Rendering.PostProcessing;
using System.IO;

/*
    TODO
    - Use a purely natural sound for the transition (no distortion).
    - Save the last prompts/positive to be able to resume between sessions and preserve the user path.
    - Add a visual indication that motion is happening.
    - Communicate that the button must be held for a direction to be taken.
    - Add music generated on udio.
    - Save the chosen icons to disk to reproduce a path at the end of the exposition.
    - Display the prompt? Use a TTS model to read out the prompt?
    - Use a llava model as an LLM to generate prompts given the current image?

    BUGS:
    - After navigating with icons a few times, sometimes the chosen icon never resolves (remains stuck in full shake).
    - Sometimes, icons remain black (with some noise).
*/
public struct Inputs
{
    public bool Left;
    public bool Right;
    public bool Up;
    public bool Down;
    public bool Plus;
    public bool Minus;
    public bool Tab;
    public bool Shift;
    public bool Space;
    public bool[] Buttons;
}

sealed record Entry
{
    public long Date;
    public string Image = "";
    public string Sound = "";
    public Colors Color;
    public int Width;
    public int Height;
    public int Rate;
    public int Samples;
    public int Channels;
    public string Positive = "";
    public string Prompt = "";
}

public sealed class Main : MonoBehaviour
{
    static readonly ((int width, int height) low, (int width, int height) high) _resolutions =
    (
        (768, 512),
        (1024, 768)
    );
    static readonly string _history = Path.Join(Application.streamingAssetsPath, "history.json");

    public RectTransform Canvas = default!;
    public Arrow Left = default!;
    public Arrow Right = default!;
    public Arrow Up = default!;
    public Arrow Down = default!;
    public Image Flash = default!;
    public Image Output = default!;
    public AudioSource In = default!;
    public AudioSource Out = default!;
    public AudioSource Select = default!;
    public AudioSource Rumble = default!;
    public AudioSource Shine = default!;
    public AudioSource Shatter = default!;
    public AudioSource Move = default!;
    public TMP_Text Statistics = default!;
    public PostProcessProfile Bloom = default!;

    float Volume => Mathf.Clamp01(_volume * _motion);

    Inputs _inputs = new() { Buttons = new bool[4] };
    Comfy.Frame _frame = new();
    Audiocraft.Clip _clip = new();
    bool _play = true;
    int _begin;
    int _end;
    float _volume = 1f;
    float _pitch = 1f;
    float _motion = 1f;
    readonly (HashSet<int> image, HashSet<int> sound) _cancel = (new(), new());

    IEnumerator Start()
    {
        var arduino = Arduino.Create();
        var comfy = Comfy.Create();
        var audiocraft = Audiocraft.Create();
        var ollama = Ollama.Create();
        yield return new WaitForSeconds(5f);

        var resolutions = (width: 0, height: 0);
        var delta = (image: 0.1f, batch: 5f, wait: 0.1f, speed: 1f);
        var deltas = (
            images: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 250).Select(_ => TimeSpan.FromSeconds(delta.image))),
            batches: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 5).Select(_ => TimeSpan.FromSeconds(delta.batch))));
        var frames = new ConcurrentQueue<Comfy.Frame>();
        var clips = new ConcurrentQueue<Audiocraft.Clip>();
        var arrows = (
            components: new[] { Left, Right, Up, Down },
            images: (queue: new ConcurrentQueue<Comfy.Icon>(), map: new ConcurrentDictionary<(int version, Tags tags), string>()),
            sounds: (queue: new ConcurrentQueue<Audiocraft.Icon>(), map: new ConcurrentDictionary<(int version, Tags tags), string>()));
        var serial = false;
        StartCoroutine(UpdateFrames());
        StartCoroutine(UpdateClips());
        StartCoroutine(UpdateArrows());
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDelta());
        StartCoroutine(UpdateDebug());

        foreach (var item in Utility.Wait(ComfyOutput(), AudiocraftOutput(), ArduinoOutput()))
        {
            Flash.color = Flash.color.With(a: Mathf.Lerp(Flash.color.a, 0f, Time.deltaTime * 5f));
            Cursor.visible = Application.isEditor;
            yield return item;
        }

        IEnumerator UpdateFrames()
        {
            var time = Time.time;
            var main = default(Texture2D);
            var load = default(Texture2D);
            while (true)
            {
                if (_play && frames.TryDequeue(out var frame))
                {
                    if (_cancel.image.Contains(frame.Version)) continue;
                    else _frame = frame;

                    while (Time.time - time < delta.wait / delta.speed) yield return null;
                    time += delta.wait / delta.speed;
                    if (comfy.memory.Load(Output, frame, ref load))
                    {
                        (main, load) = (load, main);
                        resolutions = (frame.Width, frame.Height);
                    }
                }
                else time = Time.time;
                yield return null;
            }
        }

        IEnumerator UpdateClips()
        {
            var main = default(AudioClip);
            var load = default(AudioClip);
            while (true)
            {
                if (clips.TryDequeue(out var clip))
                {
                    if (_cancel.sound.Contains(clip.Version)) continue;
                    else _clip = clip;

                    if (audiocraft.memory.Load(clip, ref load))
                    {
                        In.clip = load;
                        In.volume = 0f;
                        (main, load) = (load, main);
                        var fade = clip.Overlap * clip.Duration;
                        while (!Utility.Chain(Out, In, Volume, _pitch, fade)) yield return null;
                        (In, Out) = (Out, In);
                    }
                }
                else
                {
                    In.volume = Mathf.Lerp(In.volume, 0, Time.deltaTime);
                    In.pitch = Mathf.Lerp(In.pitch, _pitch, Time.deltaTime);
                    Out.volume = Mathf.Lerp(Out.volume, Volume, Time.deltaTime);
                    Out.pitch = Mathf.Lerp(Out.pitch, _pitch, Time.deltaTime);
                }

                yield return null;
            }
        }

        IEnumerator UpdateArrows()
        {
            while (true)
            {
                if (arrows.images.queue.TryDequeue(out var icon))
                    foreach (var arrow in arrows.components)
                        comfy.memory.Load(arrow, icon);
                if (arrows.sounds.queue.TryDequeue(out var sound))
                    foreach (var arrow in arrows.components)
                        audiocraft.memory.Load(arrow, sound);
                yield return null;
            }
        }

        IEnumerator UpdateDelta()
        {
            while (true)
            {
                delta.image = deltas.images.Select(delta => (float)delta.TotalSeconds).Average();
                delta.batch = deltas.batches.Select(delta => (float)delta.TotalMinutes).Average();
                delta.wait = Mathf.Lerp(delta.wait, delta.image, Time.deltaTime);

                var rate = 10f / delta.wait;
                var ratio = Mathf.Pow(Mathf.Clamp01(frames.Count / rate), 2f);
                delta.speed = Mathf.Lerp(delta.speed, 0.5f + ratio, Time.deltaTime * 0.25f);
                yield return null;
            }
        }

        IEnumerator UpdateDebug()
        {
            var show = Application.isEditor;
            while (true)
            {
                if (_inputs.Tab.Take()) show = !show;
                if (show)
                {
                    Statistics.text = $@"
Images Per Second: {1f / delta.image:00.000}
Batches Per Minute: {1f / delta.batch:00.000}
Rate: {delta.speed / delta.wait:00.000}
Wait: {delta.wait:0.0000}
Speed: {delta.speed:0.0000}
Frames: {frames.Count:0000}
Clips: {clips.Count:0000}
Resolution: {resolutions.width}x{resolutions.height}";
                }
                else
                    Statistics.text = "";
                yield return null;
            }
        }

        IEnumerator UpdateState()
        {
            var task = Load();
            foreach (var item in Utility.Wait(task)) yield return item;
            var entry = task.Result;
            var speed = 3.75f;
            var styles = Utility.Styles("ultra detailed", "hyper realistic", "complex", "dense", "sharp");
            var positive = entry?.Positive ?? string.Join(", ", Inspire.Image.Random(25));
            var prompt = entry?.Prompt ?? string.Join(", ", Inspire.Sound.Random(10));
            var negative = string.Join(", ", "low detail", "plain", "simple", "sparse", "blurry", "worst quality", "nude", "nudity", "naked", "genital", "child", "children", "teenager");
            var frame = new Comfy.State()
            {
                Tags = Tags.Frame,
                Width = _resolutions.high.width,
                Height = _resolutions.high.height,
                Loop = true,
                Zoom = 96,
                Steps = 5,
                Guidance = 5.5f,
                Denoise = 0.6f,
                Negative = negative,
                Full = true,
            };
            var clip = new Audiocraft.State()
            {
                Tags = Tags.Clip,
                Loop = true,
                Duration = 10f,
                Overlap = 0.5f
            };
            var loops = (
                image: Comfy.Write(comfy.process, version =>
                    entry is null ? frame with { Version = version, Positive = positive, Empty = true } :
                    frame with { Version = version, Positive = positive, Data = entry.Image, Shape = (entry.Width, entry.Height) }),
                sound: Audiocraft.Write(audiocraft.process, version =>
                    entry is null ? clip with { Version = version, Prompts = new[] { prompt }, Empty = true } :
                    clip with { Version = version, Prompts = new[] { prompt }, Data = entry.Sound })
            );
            var pause = false;
            var bloom = Bloom.GetSetting<Bloom>();
            var previous = GenerateIcons(0, positive, negative, Task.FromResult(Array.Empty<Ollama.Generation>()));
            var view = Canvas.LocalRectangle();
            var choice = (version: 0, positive, prompt, chosen: default(Arrow));
            while (true)
            {
                var inputs = serial ? _inputs.Buttons : new[] { _inputs.Right, _inputs.Left, _inputs.Up, _inputs.Down };
                UpdateIcon(Left, arrows.components, speed, 1, inputs, loops.image, position => position.With(x: 0f), position => position.With(x: -view.width / 2 - 64), position => position.With(x: -view.width * 8));
                UpdateIcon(Right, arrows.components, speed, 0, inputs, loops.image, position => position.With(x: 0f), position => position.With(x: view.width / 2 + 64), position => position.With(x: view.width * 8));
                UpdateIcon(Up, arrows.components, speed, 2, inputs, loops.image, position => position.With(y: 0f), position => position.With(y: view.height / 2 + 64), position => position.With(y: view.height * 8));
                UpdateIcon(Down, arrows.components, speed, 3, inputs, loops.image, position => position.With(y: 0f), position => position.With(y: -view.height / 2 - 64), position => position.With(y: -view.height * 8));

                switch (pause, clips.Count(clip => clip.Version == loops.sound))
                {
                    case (false, > 5):
                        Audiocraft.Write(audiocraft.process, version => new() { Version = version, Pause = new[] { loops.sound } });
                        pause = true;
                        break;
                    case (true, < 5):
                        Audiocraft.Write(audiocraft.process, version => new() { Version = version, Resume = new[] { loops.sound } });
                        pause = false;
                        break;
                }

                switch ((choice.chosen, arrows.components.FirstOrDefault(arrow => arrow.Moving)))
                {
                    // Begin choice.
                    case (null, { Icons: ({ } image, { } sound) } moving):
                        _play = false;
                        Select.PlayWith(pitch: (0.75f, 1.5f));
                        Rumble.Play();
                        Shine.Play();
                        moving.Sound.Play();
                        bloom.intensity.value = 0f;
                        bloom.color.value = Color.white;
                        choice.chosen = moving;
                        choice.positive = $"{styles} ({moving.Color}) {image.Description}";
                        choice.prompt = $"({moving.Color}) {sound.Description}";
                        choice.version = Comfy.Write(comfy.process, version => Comfy.State.Sequence(
                            frame with
                            {
                                Version = version,
                                Tags = frame.Tags | Tags.Move,
                                Loop = false,
                                Offset = _frame.Offset,
                                Size = _frame.Size,
                                Generation = _frame.Generation,
                                Shape = (_frame.Width, _frame.Height),
                                Width = _resolutions.low.width,
                                Height = _resolutions.low.height,
                                Steps = 5,
                                Guidance = 6f,
                                Denoise = 0.4f,
                                Pause = new[] { loops.image },
                                Left = Math.Max(-moving.Direction.x, 0),
                                Right = Math.Max(moving.Direction.x, 0),
                                Top = Math.Max(moving.Direction.y, 0),
                                Bottom = Math.Max(-moving.Direction.y, 0),
                            },
                            state => state with { Tags = state.Tags | Tags.Begin, Positive = $"{positive}" },
                            state => state with { Positive = $"{positive} {choice.positive}" },
                            state => state with { Positive = $"{choice.positive} {positive}" },
                            state => state with { Tags = state.Tags | Tags.End, Positive = $"{choice.positive}" },
                            _ => frame with { Version = version, Positive = $"{choice.positive}" }
                        ));
                        Debug.Log($"MAIN: Begin choice '{choice}' with frame '{_frame}'.");
                        break;
                    // Continue choice.
                    case ({ Chosen: false } chosen, var moving) when chosen == moving:
                        _play = false;
                        _volume = Mathf.Lerp(_volume, 0f, Time.deltaTime * speed);
                        var time = Mathf.Max(chosen.Time - 3.75f, 0f);
                        Rumble.pitch = Mathf.Lerp(Rumble.pitch, 0.25f, Time.deltaTime * speed);
                        Shine.volume = Mathf.Lerp(Shine.volume, time / 5f, Time.deltaTime * speed);
                        Output.color = Color.Lerp(Output.color, new(0.25f, 0.25f, 0.25f, 1f), Time.deltaTime * speed);
                        bloom.intensity.value = Mathf.Lerp(bloom.intensity.value, time * 10f, Time.deltaTime / speed);
                        bloom.color.value = Color.Lerp(bloom.color.value, chosen.Color.Color() * 10f, Time.deltaTime / speed);
                        break;
                    // End choice.
                    case ({ Chosen: true, Icons: ({ } image, { } sound) } chosen, var moving) when chosen == moving:
                        if (_begin >= choice.version)
                        {
                            Debug.Log($"MAIN: End choice '{choice}'.");
                            _play = true;
                            _motion = -1f;
                            _cancel.image.Add(loops.image);
                            _cancel.sound.Add(loops.sound);
                            Flash.color = chosen.Color.Color();
                            Shatter.Play();
                            Rumble.Stop();
                            Move.Play();
                            bloom.intensity.value = 25f;
                            bloom.color.value = chosen.Color.Color() * 25f;
                            Comfy.Write(comfy.process, version => new() { Version = version, Cancel = new[] { loops.image } });
                            loops.sound = Audiocraft.Write(audiocraft.process, version => clip with
                            {
                                Version = version,
                                Offset = sound.Offset,
                                Size = sound.Size,
                                Generation = sound.Generation,
                                // If the sound could not be loaded (ex: overwritten), allow generation from scratch.
                                Empty = true,
                                Cancel = new[] { loops.sound },
                                Prompts = new[] { choice.prompt }
                            });
                            loops.image = choice.version;
                            positive = choice.positive;
                            prompt = choice.prompt;
                            choice = (0, positive, prompt, null);
                            previous = GenerateIcons(loops.image, positive, negative, previous);
                            foreach (var arrow in arrows.components) arrow.Hide();
                            _ = Save(image, sound, chosen.Color, positive, prompt);
                        }
                        break;
                    // Cancel choice.
                    case ({ } chosen, var moving) when chosen != moving:
                        Debug.Log($"MAIN: Cancel choice '{choice}'.");
                        _play = true;
                        _cancel.image.Add(choice.version);
                        Rumble.Stop();
                        Comfy.Write(comfy.process, version => new() { Version = version, Resume = new[] { loops.image }, Cancel = new[] { choice.version } });
                        choice = (0, positive, prompt, null);
                        break;
                    case (null, null):
                        _play = true;
                        _volume = Mathf.Lerp(_volume, 1f, Time.deltaTime * speed);
                        _motion = Mathf.Lerp(_motion, 1f, Time.deltaTime / speed / speed);
                        Output.color = Color.Lerp(Output.color, Color.white, Time.deltaTime * speed);
                        Rumble.pitch = Mathf.Lerp(Rumble.pitch, 0.1f, Time.deltaTime);
                        Shine.volume = Mathf.Lerp(Shine.volume, 0f, Time.deltaTime);
                        bloom.intensity.value = Mathf.Lerp(bloom.intensity.value, 0f, Time.deltaTime);
                        bloom.color.value = Color.Lerp(bloom.color.value, Color.white, Time.deltaTime);
                        break;
                }
                yield return null;
            }

            Task<Ollama.Generation[]> GenerateIcons(int image, string positive, string negative, Task<Ollama.Generation[]> previous) => Task.WhenAll(arrows.components.Select(async arrow =>
            {
                var random = new System.Random();
                var generation = await ollama.client.Generate(arrow.Color, await previous);
                await Task.WhenAll(
                    Task.Run(async () =>
                    {
                        while (_end < image) await Task.Delay(100);
                        Comfy.Write(comfy.process, version =>
                        {
                            var tags = Tags.Icon | arrow.Tags;
                            var positive = $"{Utility.Styles("icon", "close up", "huge", "simple", "minimalistic", "figurative")} ({arrow.Color}) {generation.Image}";
                            arrows.images.map.TryAdd((version, tags), generation.Image);
                            return new()
                            {
                                Version = version,
                                Tags = tags,
                                Load = $"{arrow.Color}.png".ToLowerInvariant(),
                                Positive = positive,
                                Negative = negative,
                                Width = 512,
                                Height = 512,
                                Steps = 5,
                                Guidance = 6f,
                                Denoise = 0.7f,
                            };
                        });
                    }),
                    Task.Run(async () =>
                    {
                        while (clips.Count < 5) await Task.Delay(100);
                        Audiocraft.Write(audiocraft.process, version =>
                        {
                            var tags = Tags.Icon | arrow.Tags;
                            var prompt = $"{Utility.Styles("punchy", "jingle", "stinger", "notification")} ({arrow.Color}) {generation.Sound}";
                            arrows.sounds.map.TryAdd((version, tags), generation.Sound);
                            return new()
                            {
                                Version = version,
                                Tags = tags,
                                Duration = 10f,
                                Empty = true,
                                Prompts = new[] { prompt },
                            };
                        });
                    })
                );

                return generation;
            }));

            void UpdateIcon(Arrow arrow, Arrow[] arrows, float speed, int index, bool[] inputs, int image, Func<Vector2, Vector2> choose, Func<Vector2, Vector2> peek, Func<Vector2, Vector2> hide)
            {
                var hidden = arrows.Any(arrow => arrow.Hidden);
                var move = !hidden && inputs[index] &&
                    inputs.Enumerate().All(pair => pair.index == index || !pair.item) &&
                    arrows.All(other => arrow == other || other.Idle);
                {
                    var source = arrow.Rectangle.anchoredPosition;
                    var target = hidden ? hide(source) : move ? choose(source) : peek(source);
                    var position = Vector2.Lerp(source, target, Time.deltaTime * speed);
                    arrow.Rectangle.anchoredPosition = position;
                }
                {
                    var alpha = move ? Mathf.Max(1f - arrow.Time, 0f) : 1f;
                    var source = arrow.Socket.Close.color;
                    var target = source.With(a: alpha);
                    var color = Color.Lerp(source, target, Time.deltaTime * speed);
                    arrow.Socket.Close.color = color;
                }
                {
                    var random = inputs[index] ? new Vector3(Random.value, Random.value) * 5f : Vector3.zero;
                    var shake = random * Mathf.Clamp(arrow.Time - 2.5f, 0f, 5f);
                    arrow.Shake.anchoredPosition = shake;
                }
                {
                    var source = arrow.Shake.localScale;
                    var target = inputs[index] ? Vector3.one + Vector3.one * Mathf.Min(Mathf.Pow(arrow.Time, 0.5f), 1.5f) : Vector3.one;
                    var scale = Vector3.Lerp(source, target, Time.deltaTime * speed);
                    arrow.Shake.localScale = scale;
                }
                {
                    arrow.Sound.volume = Mathf.Lerp(arrow.Sound.volume, move ? 1f : 0f, Time.deltaTime * speed);
                    arrow.Sound.pitch = Mathf.Lerp(arrow.Sound.pitch, move ? 1f : 0.1f, Time.deltaTime * speed * speed);
                }
                arrow.Time = hidden ? 0f : move ? arrow.Time + Time.deltaTime : 0f;
            }
        }

        async Task ComfyOutput()
        {
            var watch = Stopwatch.StartNew();
            var then = (image: watch.Elapsed, batch: watch.Elapsed);
            while (!comfy.process.HasExited)
            {
                var line = await comfy.process.StandardOutput.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var index = 0;
                    var splits = line.Split(",", StringSplitOptions.None);
                    var version = int.Parse(splits[index++]);
                    var tags = (Tags)int.Parse(splits[index++]);
                    var width = int.Parse(splits[index++]);
                    var height = int.Parse(splits[index++]);
                    var count = int.Parse(splits[index++]);
                    var offset = int.Parse(splits[index++]);
                    var size = int.Parse(splits[index++]) / count;
                    var generation = int.Parse(splits[index++]);
                    if (tags.HasFlag(Tags.Begin)) _begin = version;
                    if (tags.HasFlag(Tags.End)) _end = version;
                    if (tags.HasFlag(Tags.Frame))
                    {
                        var frame = new Comfy.Frame
                        {
                            Version = version,
                            Generation = generation,
                            Count = count,
                            Width = width,
                            Height = height,
                            Size = size,
                            Tags = tags,
                        };
                        Comfy.Log($"Received frame: {frame}");
                        for (int i = 0; i < count; i++, offset += size)
                        {
                            frames.Enqueue(frame with { Index = i, Offset = offset });

                            var now = watch.Elapsed;
                            if (deltas.images.TryDequeue(out _)) deltas.images.Enqueue(now - then.image);
                            then.image = now;
                        }
                        {
                            var now = watch.Elapsed;
                            if (deltas.batches.TryDequeue(out _)) deltas.batches.Enqueue(now - then.batch);
                            then.batch = now;
                        }
                    }
                    if (arrows.images.map.TryRemove((version, tags), out var description))
                    {
                        var icon = new Comfy.Icon
                        {
                            Version = version,
                            Generation = generation,
                            Width = width,
                            Height = height,
                            Offset = offset,
                            Size = size,
                            Tags = tags,
                            Description = description,
                        };
                        Comfy.Log($"Received icon: {icon}");
                        arrows.images.queue.Enqueue(icon);
                    }
                }
                catch (FormatException) { Comfy.Warn(line); }
                catch (IndexOutOfRangeException) { Comfy.Warn(line); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        async Task AudiocraftOutput()
        {
            while (!audiocraft.process.HasExited)
            {
                var line = await audiocraft.process.StandardOutput.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var index = 0;
                    var splits = line.Split(",", StringSplitOptions.None);
                    var version = int.Parse(splits[index++]);
                    var tags = (Tags)int.Parse(splits[index++]);
                    var overlap = float.Parse(splits[index++]);
                    var rate = int.Parse(splits[index++]);
                    var samples = int.Parse(splits[index++]);
                    var channels = int.Parse(splits[index++]);
                    var count = int.Parse(splits[index++]);
                    var offset = int.Parse(splits[index++]);
                    var size = int.Parse(splits[index++]) / count;
                    var generation = int.Parse(splits[index++]);
                    if (tags.HasFlag(Tags.Clip))
                    {
                        var clip = new Audiocraft.Clip
                        {
                            Version = version,
                            Tags = tags,
                            Rate = rate,
                            Overlap = overlap,
                            Samples = samples,
                            Channels = channels,
                            Count = count,
                            Offset = offset,
                            Size = size,
                            Generation = generation,
                        };
                        Audiocraft.Log($"Received clip: {clip}");
                        for (int i = 0; i < count; i++, offset += size)
                            clips.Enqueue(clip with { Index = i, Offset = offset });
                    }
                    if (arrows.sounds.map.TryRemove((version, tags), out var description))
                    {
                        var icon = new Audiocraft.Icon
                        {
                            Version = version,
                            Tags = tags,
                            Rate = rate,
                            Samples = samples,
                            Channels = channels,
                            Offset = offset,
                            Size = size,
                            Generation = generation,
                            Description = description,
                        };
                        Audiocraft.Log($"Received icon: {icon}");
                        arrows.sounds.queue.Enqueue(icon);
                    }
                }
                catch (FormatException) { Audiocraft.Warn(line); }
                catch (IndexOutOfRangeException) { Audiocraft.Warn(line); }
                catch (Exception exception) { Debug.LogException(exception); }
            }
        }

        async Task ArduinoOutput()
        {
            var index = 0;
            var buffer = new byte[32];
            while (arduino is { IsConnected: true, Stream: var stream })
            {
                var count = await stream.ReadAsync(buffer);
                for (int i = 0; i < count; i++)
                {
                    if (buffer[i] == byte.MaxValue) { index = 0; serial = true; }
                    else if (index < _inputs.Buttons.Length) _inputs.Buttons[index++] = buffer[i] > 0;
                }
            }
        }

        async Task Save(Comfy.Icon image, Audiocraft.Icon sound, Colors color, string positive, string prompt) =>
            await File.AppendAllLinesAsync(_history, new[] { JsonUtility.ToJson(new Entry
            {
                Date = DateTime.UtcNow.Ticks,
                Image = Convert.ToBase64String(comfy.memory.Read(image.Offset, image.Size)),
                Sound = Convert.ToBase64String(audiocraft.memory.Read(sound.Offset, sound.Size)),
                Color = color,
                Width = image.Width,
                Height = image.Height,
                Rate = sound.Rate,
                Channels = sound.Channels,
                Samples = sound.Samples,
                Positive = positive,
                Prompt = prompt,
            }) });

        async Task<Entry?> Load() =>
            File.Exists(_history) && await File.ReadAllLinesAsync(_history) is { Length: > 0 } lines ?
            JsonUtility.FromJson<Entry>(lines[^1]) : null;
    }

    void Update()
    {
        _inputs.Left = Input.GetKey(KeyCode.LeftArrow);
        _inputs.Right = Input.GetKey(KeyCode.RightArrow);
        _inputs.Up = Input.GetKey(KeyCode.UpArrow);
        _inputs.Down = Input.GetKey(KeyCode.DownArrow);
        _inputs.Plus = Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals);
        _inputs.Minus = Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Underscore);
        _inputs.Tab = Input.GetKeyDown(KeyCode.Tab);
        _inputs.Shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        _inputs.Space = Input.GetKeyDown(KeyCode.Space);
    }
}