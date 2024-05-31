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
using System.Threading;
using Random = UnityEngine.Random;
using System.Collections.Generic;

/*
    TODO
    - Can I run a comfy instance on the CPU?
    - Make the prompt LLM generate more diverse visual styles and subjects.
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
public sealed class Main : MonoBehaviour
{
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

    public sealed record State
    {
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

        public override string ToString() =>
            $@"{{""version"":{Version},""loop"":{(Loop ? "True" : "False")},""tags"":{(int)Tags},""width"":{Width},""height"":{Height},""offset"":{Offset},""size"":{Size},""generation"":{Generation},""shape"":({Shape.height}, {Shape.width}),""left"":{Left},""right"":{Right},""bottom"":{Bottom},""top"":{Top},""zoom"":{Zoom},""steps"":{Steps},""guidance"":{Guidance},""denoise"":{Denoise},""full"":{(Full ? "True" : "False")},""cancel"":[{string.Join(",", Cancel ?? Array.Empty<int>())}],""pause"":[{string.Join(",", Pause ?? Array.Empty<int>())}],""resume"":[{string.Join(",", Resume ?? Array.Empty<int>())}],""empty"":{(Empty ? "True" : "False")},""positive"":""{Escape(Positive)}"",""negative"":""{Escape(Negative)}"",""load"":""{Load}"",""next"":{Next?.ToString() ?? "None"}}}";
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

    static readonly ((int width, int height) low, (int width, int height) high) _resolutions =
    (
        (768, 512),
        (896, 640)
    );

    static int _version = 0;

    public RectTransform Canvas = default!;
    public Arrow Left = default!;
    public Arrow Right = default!;
    public Arrow Up = default!;
    public Arrow Down = default!;
    public Image Flash = default!;
    public Image Output = default!;
    public TMP_Text Statistics = default!;

    Inputs _inputs = default;
    Frame _frame = new();
    bool _play = true;
    int _begin;
    int _end;
    (int stop, HashSet<int> set) _cancel = (0, new());

    IEnumerator Start()
    {
        using var memory = Utility.Memory();
        using var comfy = Utility.Docker("comfy");
        var ollama = Utility.Ollama();
        using var __ = ollama.process;
        using var ___ = ollama.client;
        yield return new WaitForSeconds(5f);

        var resolutions = (width: 0, height: 0);
        var delta = (image: 0.1f, batch: 5f, wait: 0.1f, speed: 1f);
        var deltas = (
            images: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 250).Select(_ => TimeSpan.FromSeconds(delta.image))),
            batches: new ConcurrentQueue<TimeSpan>(Enumerable.Range(0, 5).Select(_ => TimeSpan.FromSeconds(delta.batch))));
        var frames = new ConcurrentQueue<Frame>();
        var icons = (
            arrows: new[] { Left, Right, Up, Down },
            queue: new ConcurrentQueue<Icon>(),
            map: new ConcurrentDictionary<(int version, Tags tags), string>());
        StartCoroutine(UpdateFrames());
        StartCoroutine(UpdateIcons());
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDelta());
        StartCoroutine(UpdateDebug());

        foreach (var item in Utility.Wait(ReadOutput()))
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
                    if (Valid(frame)) _frame = frame;
                    else continue;

                    while (Time.time - time < delta.wait / delta.speed) yield return null;
                    time += delta.wait / delta.speed;
                    if (Utility.Load(memory, Output, frame, ref load))
                    {
                        (main, load) = (load, main);
                        resolutions = (frame.Width, frame.Height);
                    }
                }
                else time = Time.time;
                yield return null;
            }
        }

        bool Valid(Frame frame) => frame.Version >= _cancel.stop && !_cancel.set.Contains(frame.Version);

        IEnumerator UpdateIcons()
        {
            while (true)
            {
                if (icons.queue.TryDequeue(out var icon))
                    foreach (var arrow in icons.arrows)
                        Utility.Load(memory, arrow, icon);
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
            var positive = $"{styles} Everything is a 'TCHOO TCHOO' train. Flesh organic locomotive speeding on vast empty nebula tracks. Eternal spiral railways in the cosmos. Coal ember engine of intricate fusion. Unholy desecrated church station. Runic glyphs neon 'TCHOO' engravings. Darkness engulfed black hole pentagram. Blood magic eldritch rituals to summon whimsy hellish trains of wonder. Everything is a 'TCHOO TCHOO' train.";
            var negative = Utility.Styles("nude", "naked", "nudity", "youth", "child", "children", "blurry", "worst quality", "low detail");
            var frame = new State()
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
            var loop = WriteInput(version => frame with { Version = version, Empty = true, Positive = positive });
            GenerateIcons(0, positive, negative);

            var view = Canvas.LocalRectangle();
            var choice = (version: 0, positive, chosen: default(Arrow));
            while (true)
            {
                var inputs = new[] { _inputs.Left.Take(), _inputs.Right.Take(), _inputs.Up.Take(), _inputs.Down.Take() };
                UpdateIcon(Left, speed, 0, inputs, position => position.With(x: 0f), position => position.With(x: -view.width / 2 - 64), position => position.With(x: -view.width * 8));
                UpdateIcon(Right, speed, 1, inputs, position => position.With(x: 0f), position => position.With(x: view.width / 2 + 64), position => position.With(x: view.width * 8));
                UpdateIcon(Up, speed, 2, inputs, position => position.With(y: 0f), position => position.With(y: view.height / 2 + 64), position => position.With(y: view.height * 8));
                UpdateIcon(Down, speed, 3, inputs, position => position.With(y: 0f), position => position.With(y: -view.height / 2 - 64), position => position.With(y: -view.height * 8));

                if (_inputs.Space.Take()) GenerateIcons(0, positive, negative);
                switch ((choice.chosen, icons.arrows.FirstOrDefault(arrow => arrow.Moving)))
                {
                    // Begin choice.
                    case (null, { Icon: { } icon } moving):
                        _play = false;
                        choice.chosen = moving;
                        choice.positive = $"{styles} ({moving.Color}) {icon.Description}";
                        choice.version = WriteInput(version => State.Sequence(
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
                        Output.color = Color.Lerp(Output.color, new(0.25f, 0.25f, 0.25f, 1f), Time.deltaTime * speed);
                        break;
                    // End choice.
                    case ({ Chosen: true } chosen, var moving) when chosen == moving:
                        if (_begin >= choice.version)
                        {
                            Debug.Log($"MAIN: End choice '{choice}'.");
                            Flash.color = Flash.color.With(a: 1f);
                            _play = true;
                            _cancel.set.Add(loop);
                            WriteInput(version => new() { Version = version, Cancel = new[] { loop } });
                            loop = choice.version;
                            positive = choice.positive;
                            choice = (0, positive, null);
                            GenerateIcons(loop, positive, negative);
                            foreach (var arrow in icons.arrows) arrow.Hide();
                        }
                        break;
                    // Cancel choice.
                    case ({ } chosen, var moving) when chosen != moving:
                        Debug.Log($"MAIN: Cancel choice '{choice}'.");
                        _play = true;
                        _cancel.set.Add(choice.version);
                        WriteInput(version => new() { Version = version, Resume = new[] { loop }, Cancel = new[] { choice.version } });
                        choice = (0, positive, null);
                        break;
                    case (null, null):
                        _play = true;
                        Output.color = Color.Lerp(Output.color, Color.white, Time.deltaTime * speed);
                        break;
                }
                yield return null;
            }

            Task GenerateIcons(int end, string positive, string negative) => Task.WhenAll(icons.arrows.Select(async arrow =>
            {
                var random = new System.Random();
                var prompt = $@"
Previous image description: '{arrow.Icon?.Description}'

You are a divergent, creative and eccentric artist that excels in telling masterful and powerful impromptu stories through image descriptions.
Write a short succinct summary description of the next wildly surreal impossible creative image.
Its themes must metaphorically loosely partially vaguely be related to the connotations and poetic imagery of the color '{arrow.Color}'.
It must follow narratively and constrastively from the previous image description.
It must include an exotic, bizarre, wonderful and nice visual style.
Favor abstract concepts and ideas rather than concrete subjects.
Your answer must strictly consist of visual styles and an image description of maximum 50 words; nothing else; no salutation, no word count, no acknowledgement, no introduction; just the description.";
                var description = await Utility.Generate(ollama.client, prompt);
                while (_end < end) await Task.Delay(100);

                WriteInput(version =>
                {
                    var tags = Tags.Icon | arrow.Tags;
                    var positive = $"(icon, close up, huge, simple, minimalistic, figurative, {arrow.Color}) {description}";
                    icons.map.TryAdd((version, tags), description);
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
                        Guidance = 3f,
                        Denoise = 0.7f,
                    };
                });
            }));

            void UpdateIcon(Arrow arrow, float speed, int index, bool[] inputs, Func<Vector2, Vector2> choose, Func<Vector2, Vector2> peek, Func<Vector2, Vector2> hide)
            {
                var hidden = icons.arrows.Any(arrow => arrow.Hidden);
                var move =
                    inputs[index] &&
                    // (inputs[index] || arrow.Preview) &&
                    inputs.Enumerate().All(pair => pair.index == index || !pair.item) &&
                    icons.arrows.All(other => arrow == other || other.Idle);
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
                arrow.Time = hidden ? 0f : move ? arrow.Time + Time.deltaTime : 0f;
            }
        }

        int WriteInput(Func<int, State> get)
        {
            var version = Interlocked.Increment(ref _version);
            var state = get(_version);
            Debug.Log($"COMFY: Sending input '{state}'.");
            comfy.StandardInput.WriteLine($"{state}");
            comfy.StandardInput.Flush();
            return version;
        }

        async Task ReadOutput()
        {
            var watch = Stopwatch.StartNew();
            var then = (image: watch.Elapsed, batch: watch.Elapsed);
            while (!comfy.HasExited)
            {
                var line = await comfy.StandardOutput.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var splits = line.Split(",", StringSplitOptions.None);
                    var index = 0;
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
                        var frame = new Frame
                        {
                            Version = version,
                            Generation = generation,
                            Count = count,
                            Width = width,
                            Height = height,
                            Size = size,
                            Tags = tags,
                        };
                        Debug.Log($"COMFY: Received frame: {frame}");
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
                    if (icons.map.TryRemove((version, tags), out var description))
                    {
                        var icon = new Icon
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
                        Debug.Log($"COMFY: Received icon: {icon}");
                        icons.queue.Enqueue(icon);
                    }
                }
                catch (FormatException) { Debug.LogWarning($"COMFY: {line}"); }
                catch (IndexOutOfRangeException) { Debug.LogWarning($"COMFY: {line}"); }
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