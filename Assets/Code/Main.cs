#nullable enable

using UnityEngine;
using System;
using System.Collections;
using System.Threading.Tasks;
using TMPro;
using System.Linq;
using UnityEngine.UI;
using Random = UnityEngine.Random;
using UnityEngine.Rendering.PostProcessing;
using System.IO;

public sealed class Main : MonoBehaviour
{
    sealed class Inputs
    {
        public bool Left { get => Arrows[1]; set => Arrows[1] = value; }
        public bool Right { get => Arrows[0]; set => Arrows[0] = value; }
        public bool Up { get => Arrows[2]; set => Arrows[2] = value; }
        public bool Down { get => Arrows[3]; set => Arrows[3] = value; }
        public bool Plus;
        public bool Minus;
        public bool Tab;
        public bool Shift;
        public bool Space;
        public readonly bool[] Arrows = new bool[4];
        public readonly bool[] Buttons = new bool[4];
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

    static readonly string _history = Path.Join(Application.streamingAssetsPath, "history.json");

    public RectTransform Canvas = default!;
    public RectTransform Shake = default!;
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

    readonly Inputs _inputs = new();

    IEnumerator Start()
    {
        var arduino = new Arduino();
        var comfy = new Comfy();
        var audiocraft = new Audiocraft();
        var ollama = new Ollama();
        yield return new WaitForSeconds(5f);

        var arrows = new[] { Left, Right, Up, Down };
        StartCoroutine(comfy.UpdateFrames(Output));
        StartCoroutine(comfy.UpdateIcons(arrows));
        StartCoroutine(comfy.UpdateDelta());
        StartCoroutine(audiocraft.UpdateClips(In, Out));
        StartCoroutine(audiocraft.UpdateIcons(arrows));
        StartCoroutine(audiocraft.UpdatePause());
        StartCoroutine(UpdateState());
        StartCoroutine(UpdateDebug());

        foreach (var item in Utility.Wait(comfy.Read(), audiocraft.Read(), arduino.Read(_inputs.Buttons)))
        {
            Cursor.visible = Application.isEditor;
            yield return item;
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
Images Per Second: {comfy.Images:00.000}
Batches Per Minute: {comfy.Batches:00.000}
Resolution: {comfy.Resolution.width}x{comfy.Resolution.height}
Rate: {comfy.Rate:00.000}
Wait: {comfy.Wait:0.0000}
Speed: {comfy.Speed:0.0000}
Frames: {comfy.Frames:0000}
Clips: {audiocraft.Clips:0000}
";
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
            comfy.WriteFrames(positive, entry?.Width, entry?.Height, entry?.Image);
            audiocraft.WriteClips(prompt, null, entry?.Sound);
            var bloom = Bloom.GetSetting<Bloom>();
            var previous = GenerateIcons(0, 0, Task.FromResult(Array.Empty<Ollama.Generation>()));
            var view = Canvas.LocalRectangle();
            var choice = (version: 0, positive, prompt, chosen: default(Arrow));
            var inputs = new bool[4];
            while (true)
            {
                Utility.Or(_inputs.Buttons, _inputs.Arrows, inputs);
                UpdateIcon(Left, arrows, speed, 1, inputs, position => position.With(x: 0f), position => position.With(x: -view.width / 2 - 64), position => position.With(x: -view.width * 8));
                UpdateIcon(Right, arrows, speed, 0, inputs, position => position.With(x: 0f), position => position.With(x: view.width / 2 + 64), position => position.With(x: view.width * 8));
                UpdateIcon(Up, arrows, speed, 2, inputs, position => position.With(y: 0f), position => position.With(y: view.height / 2 + 64), position => position.With(y: view.height * 8));
                UpdateIcon(Down, arrows, speed, 3, inputs, position => position.With(y: 0f), position => position.With(y: -view.height / 2 - 64), position => position.With(y: -view.height * 8));

                switch ((choice.chosen, arrows.FirstOrDefault(arrow => arrow.Moving)))
                {
                    // Begin choice.
                    case (null, { Icons: ({ } image, { } sound) } moving):
                        comfy.Set(play: false);
                        Select.PlayWith(pitch: (0.75f, 1.5f));
                        Rumble.Play();
                        Shine.Play();
                        moving.Sound.Play();
                        Flash.color = moving.Color.Color().With(a: 0f);
                        bloom.intensity.value = 0f;
                        bloom.color.value = Color.white;
                        choice.chosen = moving;
                        choice.positive = $"{styles} ({moving.Color}) {image.Description}";
                        choice.prompt = $"({moving.Color}) {sound.Description}";
                        choice.version = comfy.WriteBegin(moving, (positive, choice.positive));
                        Utility.Log(nameof(Main), $"Begin choice '{choice}'.");
                        break;
                    // End choice.
                    case ({ Icons: ({ } image, { } sound), Color: var color } chosen, var moving) when chosen == moving && comfy.Has(begin: choice.version):
                        Utility.Log(nameof(Main), $"End choice '{choice}'.");
                        Flash.color = color.Color(0.25f);
                        bloom.intensity.value = 25f;
                        bloom.color.value = color.Color(0.25f) * 25f;
                        Shatter.Play();
                        Rumble.Stop();
                        Move.Play();
                        comfy.Set(play: true);
                        comfy.WriteEnd();
                        audiocraft.Set(motion: -1f);
                        audiocraft.WriteClips(choice.prompt, sound, null);
                        positive = choice.positive;
                        prompt = choice.prompt;
                        previous = GenerateIcons(choice.version, Array.IndexOf(arrows, chosen), previous);
                        choice = (0, positive, prompt, null);
                        foreach (var arrow in arrows) arrow.Hide();
                        _ = Save(chosen, image, sound, positive, prompt);
                        break;
                    // Continue choice.
                    case ({ Color: var color, Time: var time } chosen, var moving) when chosen == moving:
                        var late = Mathf.Max(time - 3.75f, 0f);
                        comfy.Set(play: false);
                        audiocraft.Set(volume: 0f, time: Time.deltaTime * speed);
                        Shake.anchoredPosition = Random.insideUnitCircle * late * speed;
                        Rumble.pitch = Mathf.Lerp(Rumble.pitch, 0.5f, Time.deltaTime * speed);
                        Shine.volume = Mathf.Lerp(Shine.volume, late / 5f, Time.deltaTime * speed);
                        Output.color = Color.Lerp(Output.color, Color.gray, Time.deltaTime * speed);
                        Flash.color = Flash.color.With(a: late / 10f);
                        bloom.intensity.value = Mathf.Lerp(bloom.intensity.value, late * 10f, Time.deltaTime / speed);
                        bloom.color.value = Color.Lerp(bloom.color.value, color.Color(0.25f) * 10f, Time.deltaTime / speed);
                        break;
                    // Cancel choice.
                    case ({ } chosen, var moving) when chosen != moving:
                        Utility.Log(nameof(Main), $"Cancel choice '{choice}'.");
                        Rumble.Stop();
                        comfy.Set(play: true);
                        comfy.WriteCancel(choice.version);
                        choice = (0, positive, prompt, null);
                        break;
                    case (null, null):
                        comfy.Set(play: true);
                        audiocraft.Set(volume: 1f, time: Time.deltaTime * speed);
                        audiocraft.Set(motion: 1f, time: Time.deltaTime / speed / speed);
                        Shake.anchoredPosition = Vector3.Lerp(Shake.anchoredPosition, new(), Time.deltaTime * speed);
                        Output.color = Color.Lerp(Output.color, Color.white, Time.deltaTime * speed);
                        Rumble.pitch = Mathf.Lerp(Rumble.pitch, 0.1f, Time.deltaTime);
                        Shine.volume = Mathf.Lerp(Shine.volume, 0f, Time.deltaTime);
                        Flash.color = Flash.color.With(a: Mathf.Lerp(Flash.color.a, 0f, Time.deltaTime * speed * speed));
                        bloom.intensity.value = Mathf.Lerp(bloom.intensity.value, 0f, Time.deltaTime * speed * speed);
                        break;
                }
                yield return null;
            }

            Task<Ollama.Generation[]> GenerateIcons(int version, int index, Task<Ollama.Generation[]> previous) => Task.WhenAll(arrows.Select(async arrow =>
            {
                var random = new System.Random();
                var generations = await previous;
                var generation = await ollama.Generate(arrow.Color, generations.At(index));
                await Task.WhenAll(
                    comfy.WriteIcon(arrow, version, generation.Image),
                    audiocraft.WriteIcon(arrow, generation.Sound));
                return generation;
            }));

            void UpdateIcon(Arrow arrow, Arrow[] arrows, float speed, int index, bool[] inputs, Func<Vector2, Vector2> choose, Func<Vector2, Vector2> peek, Func<Vector2, Vector2> hide)
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

        async Task Save(Arrow arrow, Comfy.Icon image, Audiocraft.Icon sound, string positive, string prompt) =>
            await File.AppendAllLinesAsync(_history, new[] { JsonUtility.ToJson(new Entry
            {
                Date = DateTime.UtcNow.Ticks,
                Image = arrow.Texture == null ? "" : Convert.ToBase64String(arrow.Texture.GetRawTextureData()),
                Sound = arrow.Sound == null ? "" : Convert.ToBase64String(arrow.Sound.clip.GetRawData()),
                Color = arrow.Color,
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