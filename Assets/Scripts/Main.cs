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

/*
    TODO
    - Generate an ambiance with 'MagNet'.
    - Generate music in smaller chunks to favor diversity and dynamism.
    - Make the prompt LLM generate more diverse visual styles and subjects.
        - List hundreds of color-inspired subjects/environments/themes/styles with GPT4 and choose 3 randomly to direct the prompt.
    - Add a visual indication that motion is happening.
    - Communicate that the button must be held for a direction to be taken.
    - Add music generated on udio.
    - Save the chosen icons to disk to reproduce a path at the end of the exposition.
    - Can I generate an audio ambiance with an AI?
    - Add SFX. How can I generate them with an AI?
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
    public bool _1;
    public bool _2;
    public bool _3;
    public bool _4;
}

public sealed class Main : MonoBehaviour
{
    static readonly ((int width, int height) low, (int width, int height) high) _resolutions =
    (
        (768, 512),
        (896, 640)
    );

    public RectTransform Canvas = default!;
    public Arrow Left = default!;
    public Arrow Right = default!;
    public Arrow Up = default!;
    public Arrow Down = default!;
    public Image Flash = default!;
    public Image Output = default!;
    public AudioSource Sound = default!;
    public AudioSource Rumble = default!;
    public AudioSource Shine = default!;
    public AudioSource Shatter = default!;
    public TMP_Text Statistics = default!;
    public PostProcessProfile Bloom = default!;

    Inputs _inputs = default;
    Comfy.Frame _frame = new();
    bool _play = true;
    int _begin;
    int _end;
    readonly HashSet<int> _cancel = new();

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
        StartCoroutine(UpdateFrames());
        StartCoroutine(UpdateClips());
        StartCoroutine(UpdateArrows());
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDelta());
        StartCoroutine(UpdateDebug());
        StartCoroutine(UpdateSerial());

        foreach (var item in Utility.Wait(ComfyOutput(), AudiocraftOutput()))
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
                    if (_cancel.Contains(frame.Version)) continue;
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
                if (clips.TryDequeue(out var clip) && audiocraft.memory.Load(clip, ref load))
                {
                    Sound.clip = load;
                    Sound.Play();
                    (main, load) = (load, main);
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
                delta.speed = Mathf.Lerp(delta.speed, 0.5f + ratio, Time.deltaTime);
                yield return null;
            }
        }

        IEnumerator UpdateSerial()
        {
            var buffer = new byte[5];
            var inputs = new bool[4];
            while (arduino is { })
            {
                if (arduino.BytesToRead >= buffer.Length)
                {
                    arduino.Read(buffer, 0, buffer.Length);
                    if (buffer[4] < byte.MaxValue)
                    {
                        while (arduino.ReadByte() < byte.MaxValue)
                        {
                            Arduino.Log("Adjusting serial buffer.");
                            yield return null;
                        }
                    }
                    else if (inputs[0].Change(buffer[0] > 0)) _inputs._1 = inputs[0];
                    else if (inputs[1].Change(buffer[1] > 0)) _inputs._2 = inputs[1];
                    else if (inputs[2].Change(buffer[2] > 0)) _inputs._3 = inputs[2];
                    else if (inputs[3].Change(buffer[3] > 0)) _inputs._4 = inputs[3];

                    if (inputs.Any(input => input)) Arduino.Log(string.Join(", ", inputs));
                }
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
Resolution: {resolutions.width}x{resolutions.height}";
                }
                else
                    Statistics.text = "";
                yield return null;
            }
        }

        IEnumerator UpdateState()
        {
            var speed = 3.75f;
            var styles = Utility.Styles("ultra detailed", "oil painting", "abstract", "conceptual", "hyper realistic", "vibrant");
            var positive = $"void";
            var prompt = "void";
            var negative = Utility.Styles("nude", "naked", "nudity", "youth", "child", "children", "blurry", "worst quality", "low detail");
            var frame = new Comfy.State()
            {
                Tags = Tags.Frame,
                Width = _resolutions.high.width,
                Height = _resolutions.high.height,
                Loop = true,
                Zoom = 64,
                Steps = 5,
                Guidance = 4f,
                Denoise = 0.6f,
                Negative = negative,
                Full = true,
            };
            var bloom = Bloom.GetSetting<Bloom>();
            var clip = new Audiocraft.State() { Tags = Tags.Clip, Duration = 10f };
            var loop = Comfy.Write(comfy.process, version => frame with { Version = version, Empty = true, Positive = positive });
            Audiocraft.Write(audiocraft.process, version => clip with { Version = version, Prompts = new[] { prompt } });
            var previous = GenerateIcons(0, positive, negative, Task.FromResult(Array.Empty<Ollama.Generation>()));
            var view = Canvas.LocalRectangle();
            var choice = (version: 0, positive, prompt, chosen: default(Arrow));
            while (true)
            {
                var inputs = new[] { _inputs.Left.Take(), _inputs.Right.Take(), _inputs.Up.Take(), _inputs.Down.Take() };
                UpdateIcon(Left, speed, 0, inputs, position => position.With(x: 0f), position => position.With(x: -view.width / 2 - 64), position => position.With(x: -view.width * 8));
                UpdateIcon(Right, speed, 1, inputs, position => position.With(x: 0f), position => position.With(x: view.width / 2 + 64), position => position.With(x: view.width * 8));
                UpdateIcon(Up, speed, 2, inputs, position => position.With(y: 0f), position => position.With(y: view.height / 2 + 64), position => position.With(y: view.height * 8));
                UpdateIcon(Down, speed, 3, inputs, position => position.With(y: 0f), position => position.With(y: -view.height / 2 - 64), position => position.With(y: -view.height * 8));

                // TODO: Remove this.
                if (_inputs.Space.Take())
                    Audiocraft.Write(audiocraft.process, version => clip with { Version = version, Prompts = new[] { prompt } });

                switch ((choice.chosen, arrows.components.FirstOrDefault(arrow => arrow.Moving)))
                {
                    // Begin choice.
                    case (null, { Icons: ({ } image, { } sound) } moving):
                        _play = false;
                        Rumble.Play();
                        Shine.Play();
                        moving.Sound.Play();
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
                                Pause = new[] { loop },
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
                        Rumble.pitch = Mathf.Lerp(Rumble.pitch, 0.25f, Time.deltaTime * speed);
                        Shine.volume = Mathf.Lerp(Shine.volume, (chosen.Time - 3.75f) / 5f, Time.deltaTime * speed);
                        Output.color = Color.Lerp(Output.color, new(0.25f, 0.25f, 0.25f, 1f), Time.deltaTime * speed);
                        Sound.volume = Mathf.Lerp(Sound.volume, 0f, Time.deltaTime * speed);
                        bloom.intensity.value = Mathf.Lerp(bloom.intensity.value, Mathf.Max((chosen.Time - 3.75f) * 5f, 0f), Time.deltaTime * speed);
                        bloom.color.value = Color.Lerp(bloom.color.value, chosen.Color.Color() * 2.5f, Time.deltaTime * speed);
                        break;
                    // End choice.
                    case ({ Chosen: true } chosen, var moving) when chosen == moving:
                        if (_begin >= choice.version)
                        {
                            Debug.Log($"MAIN: End choice '{choice}'.");
                            _play = true;
                            _cancel.Add(loop);
                            Flash.color = Flash.color.With(a: 1f);
                            Rumble.Stop();
                            Shatter.Play();
                            bloom.intensity.value = 10f;
                            bloom.color.value = chosen.Color.Color() * 10f;
                            Comfy.Write(comfy.process, version => new() { Version = version, Cancel = new[] { loop } });
                            // TODO: Improve this.
                            Audiocraft.Write(audiocraft.process, version => clip with { Version = version, Prompts = new[] { prompt } });
                            // Audiocraft.Write(audiocraft.process, version => clip with { Version = version, Prompts = new[] { choice.prompt } });
                            loop = choice.version;
                            positive = choice.positive;
                            prompt = choice.prompt;
                            choice = (0, positive, prompt, null);
                            previous = GenerateIcons(loop, positive, negative, previous);
                            foreach (var arrow in arrows.components) arrow.Hide();
                        }
                        break;
                    // Cancel choice.
                    case ({ } chosen, var moving) when chosen != moving:
                        Debug.Log($"MAIN: Cancel choice '{choice}'.");
                        _play = true;
                        _cancel.Add(choice.version);
                        Rumble.Stop();
                        Comfy.Write(comfy.process, version => new() { Version = version, Resume = new[] { loop }, Cancel = new[] { choice.version } });
                        choice = (0, positive, prompt, null);
                        break;
                    case (null, null):
                        _play = true;
                        Output.color = Color.Lerp(Output.color, Color.white, Time.deltaTime * speed);
                        Sound.volume = Mathf.Lerp(Sound.volume, 1f, Time.deltaTime * speed);
                        Rumble.pitch = Mathf.Lerp(Rumble.pitch, 0.1f, Time.deltaTime);
                        Shine.volume = Mathf.Lerp(Shine.volume, 0f, Time.deltaTime);
                        bloom.intensity.value = Mathf.Lerp(bloom.intensity.value, 0f, Time.deltaTime * speed);
                        bloom.color.value = Color.Lerp(bloom.color.value, Color.white, Time.deltaTime * speed);
                        break;
                }
                yield return null;
            }

            Task<Ollama.Generation[]> GenerateIcons(int end, string positive, string negative, Task<Ollama.Generation[]> previous) => Task.WhenAll(arrows.components.Select(async arrow =>
            {
                var random = new System.Random();
                var generation = await ollama.client.Generate(arrow.Color, await previous);
                while (_end < end) await Task.Delay(100);

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
                        Prompts = new[] { prompt },
                    };
                });
                return generation;
            }));

            void UpdateIcon(Arrow arrow, float speed, int index, bool[] inputs, Func<Vector2, Vector2> choose, Func<Vector2, Vector2> peek, Func<Vector2, Vector2> hide)
            {
                var hidden = arrows.components.Any(arrow => arrow.Hidden);
                var move =
                    inputs[index] &&
                    inputs.Enumerate().All(pair => pair.index == index || !pair.item) &&
                    arrows.components.All(other => arrow == other || other.Idle);
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
                    var random = inputs[index] ? new Vector3(Random.value, Random.value) * 3.75f : Vector3.zero;
                    var shake = random * Mathf.Clamp(arrow.Time - 2.5f, 0f, 5f);
                    arrow.Shake.anchoredPosition = shake;
                }
                {
                    var source = arrow.Shake.localScale;
                    var target = inputs[index] ? Vector3.one + Vector3.one * Mathf.Min(Mathf.Pow(arrow.Time / 3.75f, 2f), 1f) : Vector3.one;
                    var scale = Vector3.Lerp(source, target, Time.deltaTime * speed);
                    arrow.Shake.localScale = scale;
                }
                {
                    arrow.Sound.volume = Mathf.Lerp(arrow.Sound.volume, move ? 1f : 0f, Time.deltaTime * speed);
                    arrow.Filter.cutoffFrequency = Mathf.Lerp(arrow.Filter.cutoffFrequency, move ? 5000f : 0f, Time.deltaTime);
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
        _inputs._1 = Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1);
        _inputs._2 = Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2);
        _inputs._3 = Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3);
        _inputs._4 = Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4);
    }
}