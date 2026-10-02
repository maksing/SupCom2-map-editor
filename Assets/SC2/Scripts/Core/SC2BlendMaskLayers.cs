using System;
using System.Collections.Generic;

/// <summary>
/// One editing layer of the terrain blend mask. A layer stores two bytes per pixel: the VALUE it paints (0 = the
/// pair's first material, 255 = the second) and its COVERAGE (how much of the pixel the layer owns, like a
/// Photoshop layer's transparency). Coverage is needed because a single channel cannot tell an untouched pixel
/// from one painted to 0. The layers of a stack are flattened bottom to top into the one alpha plane the terrain
/// shader and the exporter read.
///
/// <see cref="Data"/> is treated as immutable once published: an edit builds a new array and replaces the
/// reference, so undo snapshots can share arrays instead of copying 8 MB per layer.
/// </summary>
[Serializable]
public class SC2BlendMaskLayer
{
    public string Id = NewId();
    public string Name = "Layer";
    public bool Visible = true;
    /// <summary>0..1, multiplies the layer's coverage when the stack is flattened.</summary>
    public float Opacity = 1f;
    /// <summary>Width * Height * 2 bytes (value, coverage). Null or empty means fully transparent.</summary>
    public byte[] Data = Array.Empty<byte>();

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>A copy with the same settings that shares <see cref="Data"/> (safe: Data is never mutated in place).</summary>
    public SC2BlendMaskLayer Clone() => new SC2BlendMaskLayer
    {
        Id = Id,
        Name = Name,
        Visible = Visible,
        Opacity = Opacity,
        Data = Data,
    };
}

/// <summary>The layers of one mask group (all pairs that share a mask name), bottom layer first.</summary>
[Serializable]
public class SC2BlendMaskLayerStack
{
    public string MaskName = "mask01";
    public int Width = 2048;
    public int Height = 2048;
    public int ActiveIndex;
    public List<SC2BlendMaskLayer> Layers = new List<SC2BlendMaskLayer>();

    public int PixelCount => Width * Height;

    public SC2BlendMaskLayer Active => Layers.Count == 0 ? null : Layers[Math.Max(0, Math.Min(ActiveIndex, Layers.Count - 1))];

    /// <summary>A copy of the stack that shares each layer's data array.</summary>
    public SC2BlendMaskLayerStack Clone()
    {
        var copy = new SC2BlendMaskLayerStack { MaskName = MaskName, Width = Width, Height = Height, ActiveIndex = ActiveIndex };
        foreach (var layer in Layers) copy.Layers.Add(layer.Clone());
        return copy;
    }

    /// <summary>Keeps the stack usable: at least one layer, a valid active index, no null layers, unique ids.</summary>
    public void Normalize()
    {
        Layers ??= new List<SC2BlendMaskLayer>();
        Layers.RemoveAll(layer => layer == null);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in Layers)
        {
            if (string.IsNullOrEmpty(layer.Id) || !seen.Add(layer.Id)) { layer.Id = SC2BlendMaskLayer.NewId(); seen.Add(layer.Id); }
            if (layer.Name == null) layer.Name = "Layer";
            layer.Opacity = Math.Max(0f, Math.Min(1f, layer.Opacity));
            layer.Data ??= Array.Empty<byte>();
        }
        if (Layers.Count == 0) Layers.Add(SC2BlendMaskLayerMath.CreateEmptyBaseLayer(PixelCount));
        ActiveIndex = Math.Max(0, Math.Min(ActiveIndex, Layers.Count - 1));
    }

    /// <summary>Adds an empty (fully transparent) layer directly above the active one and makes it active.</summary>
    public SC2BlendMaskLayer AddLayerAboveActive(string name = null)
    {
        var layer = new SC2BlendMaskLayer { Name = name ?? NextLayerName() };
        var index = Layers.Count == 0 ? 0 : Math.Min(ActiveIndex, Layers.Count - 1) + 1;
        Layers.Insert(index, layer);
        ActiveIndex = index;
        return layer;
    }

    /// <summary>Copies the layer at <paramref name="index"/> directly above itself and makes the copy active.</summary>
    public SC2BlendMaskLayer DuplicateLayer(int index)
    {
        if (index < 0 || index >= Layers.Count) return null;
        var copy = Layers[index].Clone();
        copy.Id = SC2BlendMaskLayer.NewId();
        copy.Name = Layers[index].Name + " copy";
        Layers.Insert(index + 1, copy);
        ActiveIndex = index + 1;
        return copy;
    }

    /// <summary>Removes a layer; the last remaining layer cannot be removed. Returns false when nothing was removed.</summary>
    public bool RemoveLayer(int index)
    {
        if (index < 0 || index >= Layers.Count || Layers.Count <= 1) return false;
        Layers.RemoveAt(index);
        // Removing the active layer selects the one below it; removing a layer under the active one keeps it selected.
        if (ActiveIndex == index || ActiveIndex > index) ActiveIndex--;
        ActiveIndex = Math.Max(0, Math.Min(ActiveIndex, Layers.Count - 1));
        return true;
    }

    /// <summary>Moves a layer one step up (toward the top of the stack, +1) or down (-1). The active layer follows a moved layer.</summary>
    public bool MoveLayer(int index, int direction)
    {
        var target = index + (direction > 0 ? 1 : -1);
        if (index < 0 || index >= Layers.Count || target < 0 || target >= Layers.Count) return false;
        (Layers[index], Layers[target]) = (Layers[target], Layers[index]);
        if (ActiveIndex == index) ActiveIndex = target;
        else if (ActiveIndex == target) ActiveIndex = index;
        return true;
    }

    /// <summary>Folds the layer at <paramref name="index"/> into the one below it (see
    /// <see cref="SC2BlendMaskLayerMath.MergeDown"/>). Returns false for the bottom layer.</summary>
    public bool MergeDown(int index)
    {
        if (index <= 0 || index >= Layers.Count) return false;
        var merged = SC2BlendMaskLayerMath.MergeDown(Layers[index], Layers[index - 1], PixelCount);
        Layers[index - 1] = merged;
        Layers.RemoveAt(index);
        ActiveIndex = index - 1;
        return true;
    }

    private string NextLayerName()
    {
        for (var n = Layers.Count + 1; ; n++)
        {
            var candidate = "Layer " + n;
            if (!Layers.Exists(layer => string.Equals(layer.Name, candidate, StringComparison.Ordinal))) return candidate;
        }
    }
}

/// <summary>Pure pixel math for the blend mask layers (no Unity types), shared by the CPU paths and the tests.</summary>
public static class SC2BlendMaskLayerMath
{
    public const int BytesPerPixel = 2;

    /// <summary>The bottom layer of a new stack: fully covering, value 0.</summary>
    public static SC2BlendMaskLayer CreateEmptyBaseLayer(int pixelCount)
    {
        var data = new byte[pixelCount * BytesPerPixel];
        for (var i = 0; i < pixelCount; i++) data[i * BytesPerPixel + 1] = 255;
        return new SC2BlendMaskLayer { Name = "Base", Data = data };
    }

    /// <summary>The Base layer of a map that was painted before layers existed: its value is the existing mask's alpha
    /// (<paramref name="rgbaMask"/> is the RGBA32 mask, alpha in every fourth byte) and it covers every pixel, so the
    /// flattened result is identical to the old mask. A missing or wrongly sized mask gives value 0.</summary>
    public static SC2BlendMaskLayer CreateBaseFromRgbaMask(byte[] rgbaMask, int pixelCount)
    {
        var layer = CreateEmptyBaseLayer(pixelCount);
        if (rgbaMask == null || rgbaMask.Length != pixelCount * 4) return layer;
        for (var i = 0; i < pixelCount; i++) layer.Data[i * BytesPerPixel] = rgbaMask[i * 4 + 3];
        return layer;
    }

    /// <summary>Flattens the visible layers, bottom to top, into <paramref name="alphaOut"/> (one byte per pixel):
    /// <c>out = lerp(out, value, coverage * opacity)</c>. Layers whose data has the wrong length are skipped.</summary>
    public static void Composite(IReadOnlyList<SC2BlendMaskLayer> layers, int pixelCount, byte[] alphaOut)
    {
        if (alphaOut == null || alphaOut.Length < pixelCount) throw new ArgumentException("alphaOut is too small.", nameof(alphaOut));
        var acc = new float[pixelCount];
        foreach (var layer in layers)
        {
            if (layer == null || !layer.Visible || layer.Data == null || layer.Data.Length != pixelCount * BytesPerPixel) continue;
            var opacity = Math.Max(0f, Math.Min(1f, layer.Opacity));
            if (opacity <= 0f) continue;
            var data = layer.Data;
            for (var i = 0; i < pixelCount; i++)
            {
                var weight = data[i * BytesPerPixel + 1] * (1f / 255f) * opacity;
                if (weight <= 0f) continue;
                acc[i] += (data[i * BytesPerPixel] * (1f / 255f) - acc[i]) * weight;
            }
        }
        for (var i = 0; i < pixelCount; i++) alphaOut[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(acc[i] * 255f)));
    }

    /// <summary>The single layer that looks exactly like <paramref name="upper"/> drawn over <paramref name="lower"/>
    /// (both their opacities and visibility are baked in, the result is visible at opacity 1 and keeps the lower
    /// layer's id, name and position). With effective coverages cl and cu: coverage = cl + cu - cl*cu and value =
    /// (cl*(1-cu)*vl + cu*vu) / coverage, which reproduces <c>lerp(lerp(b, vl, cl), vu, cu)</c> for any background b.</summary>
    public static SC2BlendMaskLayer MergeDown(SC2BlendMaskLayer upper, SC2BlendMaskLayer lower, int pixelCount)
    {
        var result = new SC2BlendMaskLayer { Id = lower.Id, Name = lower.Name, Visible = true, Opacity = 1f, Data = new byte[pixelCount * BytesPerPixel] };
        var lowerData = EffectiveOrNull(lower, pixelCount);
        var upperData = EffectiveOrNull(upper, pixelCount);
        var lowerOpacity = lower.Visible ? Math.Max(0f, Math.Min(1f, lower.Opacity)) : 0f;
        var upperOpacity = upper.Visible ? Math.Max(0f, Math.Min(1f, upper.Opacity)) : 0f;
        for (var i = 0; i < pixelCount; i++)
        {
            float vl = 0f, cl = 0f, vu = 0f, cu = 0f;
            if (lowerData != null) { vl = lowerData[i * BytesPerPixel] * (1f / 255f); cl = lowerData[i * BytesPerPixel + 1] * (1f / 255f) * lowerOpacity; }
            if (upperData != null) { vu = upperData[i * BytesPerPixel] * (1f / 255f); cu = upperData[i * BytesPerPixel + 1] * (1f / 255f) * upperOpacity; }
            var coverage = cl + cu - cl * cu;
            var value = coverage > 0f ? (cl * (1f - cu) * vl + cu * vu) / coverage : 0f;
            result.Data[i * BytesPerPixel] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value * 255f)));
            result.Data[i * BytesPerPixel + 1] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(coverage * 255f)));
        }
        return result;
    }

    private static byte[] EffectiveOrNull(SC2BlendMaskLayer layer, int pixelCount) =>
        layer.Data != null && layer.Data.Length == pixelCount * BytesPerPixel ? layer.Data : null;
}
