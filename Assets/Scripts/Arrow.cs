#nullable enable

using System;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

[Serializable]
public struct Socket
{
    public RectTransform Rectangle;
    public Image Open;
    public Image Close;
}

public enum Colors
{
    Cyan,
    Orange,
    Purple,
    Green,
    White,
    Red,
    Yellow
}

[Flags]
public enum Tags
{
    Frame = 1 << 0,
    Icon = 1 << 1,
    Clip = 1 << 2,
    Left = 1 << 3,
    Right = 1 << 4,
    Up = 1 << 5,
    Down = 1 << 6,
    Begin = 1 << 7,
    End = 1 << 8,
    Move = 1 << 9
}

public sealed class Arrow : MonoBehaviour
{
    static float Value => Random.value * 1000f;

    public Tags Tags;
    public Colors Color;
    public string[] Themes = { };
    public Vector2Int Direction;
    public RectTransform Rectangle = default!;
    public RectTransform Shake = default!;
    public Socket Socket = default!;
    public Image Image = default!;
    public Mask Content = default!;
    public AudioSource Source = default!;
    public AudioLowPassFilter Filter = default!;

    public float Time { get; set; }
    public Comfy.Icon? Icon { get; set; }
    public Audiocraft.Clip? Clip { get; set; }
    public Texture2D? Texture { get; set; }
    public AudioClip? Audio { get; set; }

    public bool Idle => Time <= 0f;
    public bool Moving => Time > 0f;
    public bool Chosen => Time >= 5f;
    public bool Hidden => Icon is null;

    (float root, float socket) _offsets;

    void Start() => _offsets = (Value, Value);

    void Update()
    {
        transform.localScale = transform.localScale.With(Sine2(2.5f, 0.01f, 1f, _offsets.root));
        Content.transform.localScale = Content.transform.localScale.With(Sine2(1.5f, 0.06f, 0.8f, _offsets.socket));
    }

    public void Hide() { Icon = null; Time = 0f; }

    float Sine(float frequency, float amplitude, float center, float offset) =>
        Mathf.Sin(UnityEngine.Time.time * frequency + offset) * amplitude + center;
    Vector2 Sine2(float frequency, float amplitude, float center, float offset) =>
        new(Sine(frequency, amplitude, center, offset), Sine(frequency, amplitude, center, offset));
}
