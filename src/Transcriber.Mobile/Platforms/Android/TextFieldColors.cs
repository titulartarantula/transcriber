using Microsoft.Maui.Handlers;

namespace Transcriber.Mobile;

/// <summary>
/// Draws every text field's cursor, selection handles and selection highlight in the accent colour, so the
/// cursor can be found in both themes. Android tints them from the theme, and the MAUI template's dark indigo
/// accent was all but invisible on a dark background (1.3:1).
/// </summary>
public static class TextFieldColors
{
    public static void Register()
    {
        EntryHandler.Mapper.AppendToMapping(nameof(TextFieldColors), (handler, _) => Tint(handler.PlatformView));
        EditorHandler.Mapper.AppendToMapping(nameof(TextFieldColors), (handler, _) => Tint(handler.PlatformView));
    }

    private static void Tint(Android.Widget.EditText field)
    {
        var accent = new Android.Graphics.Color(field.Context!.GetColor(Resource.Color.colorAccent));
        field.TextCursorDrawable = Tinted(field.TextCursorDrawable, accent);
        field.TextSelectHandle = Tinted(field.TextSelectHandle, accent);
        field.TextSelectHandleLeft = Tinted(field.TextSelectHandleLeft, accent);
        field.TextSelectHandleRight = Tinted(field.TextSelectHandleRight, accent);
        field.SetHighlightColor(new Android.Graphics.Color(accent.R, accent.G, accent.B, (byte)0x66));
    }

    // Mutate first: the drawables are shared between fields.
    private static Android.Graphics.Drawables.Drawable? Tinted(Android.Graphics.Drawables.Drawable? drawable, Android.Graphics.Color color)
    {
        var copy = drawable?.Mutate();
        copy?.SetTint(color);
        return copy;
    }
}
