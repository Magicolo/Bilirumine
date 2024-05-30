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

public sealed class Arrow : MonoBehaviour
{
    static float Value => Random.value * 1000f;

    public Main.Tags Tags;
    public Colors Color;
    public string[] Themes = { };
    public Vector2Int Direction;
    public RectTransform Rectangle = default!;
    public RectTransform Shake = default!;
    public Socket Socket = default!;
    public Image Image = default!;
    public Mask Content = default!;

    [Header("Debug")]
    public float Time;
    public string? Description;
    public int[]? Context;
    public Texture2D? Texture;

    public bool Idle => Time <= 0f;
    public bool Preview => Moving && Time <= 3.75f;
    public bool Moving => Time > 0f;
    public bool Chosen => Time >= 7.5f;
    public bool Hidden => Context is null or { Length: 0 } || string.IsNullOrWhiteSpace(Description);

    (float root, float socket) _offsets;

    void Start() => _offsets = (Value, Value);

    void Update()
    {
        transform.localScale = transform.localScale.With(Sine2(2.5f, 0.01f, 1f, _offsets.root));
        Content.transform.localScale = Content.transform.localScale.With(Sine2(1.5f, 0.06f, 0.8f, _offsets.socket));
    }

    public void Hide() { Context = null; Description = null; Time = 0f; }

    float Sine(float frequency, float amplitude, float center, float offset) =>
        Mathf.Sin(UnityEngine.Time.time * frequency + offset) * amplitude + center;
    Vector2 Sine2(float frequency, float amplitude, float center, float offset) =>
        new(Sine(frequency, amplitude, center, offset), Sine(frequency, amplitude, center, offset));
}
