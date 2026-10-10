using UnityEngine;

/// <summary>
/// The one place text sizes come from. Every font in the editor takes one of these five roles instead of a number of its own, so the
/// same kind of text is the same size everywhere. The sizes are in design pixels (what is seen at UI Scale 1.0); the UI Scale from
/// the View menu is applied on top.
/// <list type="bullet">
/// <item><see cref="Title"/> 16: titles of dialogs and overlays.</item>
/// <item><see cref="Nav"/> 14: the menu bar, the main tabs, window and list titles.</item>
/// <item><see cref="Body"/> 13: labels, buttons, fields, sub-tabs, list rows and notes.</item>
/// <item><see cref="Caption"/> 11: section headings, hints, status text and small secondary text.</item>
/// <item><see cref="Micro"/> 10: key chips and badges.</item>
/// </list>
/// </summary>
public static class SC2Type
{
    public const int TitlePx = 16;
    public const int NavPx = 14;
    public const int BodyPx = 13;
    public const int CaptionPx = 11;
    public const int MicroPx = 10;

    private static int Size(int designPixels) => Mathf.Max(1, Mathf.RoundToInt(designPixels * SC2UiScale.Scale));

    public static int Title => Size(TitlePx);
    public static int Nav => Size(NavPx);
    public static int Body => Size(BodyPx);
    public static int Caption => Size(CaptionPx);
    public static int Micro => Size(MicroPx);
}
