using Godot;

namespace CrimsonVR;

/// <summary>Thin wrapper around the Android host plugin that gives Quest's IME
/// a genuine Android EditText. Godot's hidden LineEdit cannot become the served
/// Android view in an immersive XR activity, so Horizon rejects its show call.</summary>
internal static class QuestTextInput
{
    private const string PluginName = "CrimsonTextInput";

    private static GodotObject? Plugin
        => OS.GetName() == "Android" && Engine.HasSingleton(PluginName)
            ? Engine.GetSingleton(PluginName)
            : null;

    public static bool IsAvailable => Plugin != null;

    public static bool Show(string initialText, int maxLength, bool roomCode)
    {
        GodotObject? plugin = Plugin;
        if (plugin == null) return false;
        plugin.Call("showKeyboard", initialText, maxLength, roomCode);
        return true;
    }

    public static bool Poll(out string text, out bool submitted)
    {
        GodotObject? plugin = Plugin;
        if (plugin == null)
        {
            text = "";
            submitted = false;
            return false;
        }
        text = plugin.Call("getText").AsString();
        submitted = plugin.Call("takeSubmitted").AsBool();
        return true;
    }

    public static void Hide() => Plugin?.Call("hideKeyboard");
}
