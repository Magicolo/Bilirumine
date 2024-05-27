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

public class Icon : MonoBehaviour
{
    public Main.Tags Tags;
    public string Load = "";
    public RectTransform Rectangle = default!;
    public Socket Socket = default!;
    public Image Content = default!;
    public Mask Mask = default!;
    public float Time { get; set; }

    float _offset;

    void Start() => _offset = Random.value * 1000f;

    void Update() => transform.localScale = transform.localScale.With(x: Sine(), y: Sine());

    float Sine() => Mathf.Sin(UnityEngine.Time.time * 2.5f + _offset) * 0.005f + 1f;
}
