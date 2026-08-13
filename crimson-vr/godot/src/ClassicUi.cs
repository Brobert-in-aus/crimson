using System;
using System.Text.Json;
using Godot;

namespace CrimsonVR;

/// <summary>
/// The classic small font (grim/fonts/small.py): smallWhite.png is a 16x16
/// grid of 16px cells indexed by latin-1 byte; per-byte advance widths come
/// from load/smallFnt.dat, baked into the manifest as small_font_widths.
/// Shared singleton — null when the assets aren't baked (menus fall back to
/// Label3D system text).
/// </summary>
public sealed class SmallFont
{
    public const int Cell = 16;
    public const int Grid = 16;

    public readonly Texture2D Texture;
    public readonly int[] Widths;

    private static SmallFont? _shared;
    private static bool _checked;

    private SmallFont(Texture2D texture, int[] widths)
    {
        Texture = texture;
        Widths = widths;
    }

    public static SmallFont? Shared()
    {
        if (_checked)
        {
            return _shared;
        }
        _checked = true;
        string texPath = AssetStore.SpritePath("smallWhite.png");
        string manifestPath = AssetStore.SpritePath("sprite_manifest.json");
        if (!Godot.FileAccess.FileExists(manifestPath))
        {
            return null;
        }
        if (AssetStore.LoadTexture(texPath) is not Texture2D tex)
        {
            return null;
        }
        try
        {
            using Godot.FileAccess f = Godot.FileAccess.Open(manifestPath, Godot.FileAccess.ModeFlags.Read);
            using var doc = JsonDocument.Parse(f.GetAsText());
            if (!doc.RootElement.TryGetProperty("small_font_widths", out JsonElement arr)
                || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 256)
            {
                return null;
            }
            var widths = new int[256];
            int i = 0;
            foreach (JsonElement w in arr.EnumerateArray())
            {
                widths[i++] = w.GetInt32();
                if (i >= 256)
                {
                    break;
                }
            }
            _shared = new SmallFont(tex, widths);
        }
        catch (JsonException)
        {
            return null;
        }
        return _shared;
    }

    /// <summary>Max line width in FONT PIXELS (measure_small_text_width).</summary>
    public float MeasureWidth(string text)
    {
        float x = 0.0f;
        float best = 0.0f;
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                best = Mathf.Max(best, x);
                x = 0.0f;
                continue;
            }
            int b = ch < 256 ? ch : '?';
            int w = Widths[b];
            if (w > 0)
            {
                x += w;
            }
        }
        return Mathf.Max(best, x);
    }
}

/// <summary>
/// 3D text rendered with the classic small font: one quad per glyph in a
/// single ArrayMesh, local XY plane facing +Z, Nearest-filtered and
/// depth-tested like all diegetic UI. Vertically centred on the node origin;
/// horizontally centred by default (left-aligned optional).
/// </summary>
public sealed partial class SmallFontLabel : MeshInstance3D
{
    private SmallFont _font = null!;
    private StandardMaterial3D _mat = null!;
    private float _pixelSize;
    private bool _center;
    private string _text = string.Empty;

    /// <param name="pixelSize">metres per font pixel (glyph line height = 16 px).</param>
    /// <param name="priority">Render priority: all diegetic UI is transparent
    /// with depth-write off, so priority IS the draw order. Panel text defaults
    /// above the panel backdrop (58) and the HUD (40-44).</param>
    public void Build(SmallFont font, float pixelSize, Color color, bool center = true, int priority = 62)
    {
        _font = font;
        _pixelSize = pixelSize;
        _center = center;
        _mat = new StandardMaterial3D
        {
            AlbedoTexture = font.Texture,
            AlbedoColor = color,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest,
            RenderPriority = priority,
        };
        MaterialOverride = _mat;
    }

    public void SetColor(Color color)
    {
        if (_mat != null)
        {
            _mat.AlbedoColor = color;
        }
    }

    public float TextWidthMeters(string text) => _font.MeasureWidth(text) * _pixelSize;

    public void SetText(string text)
    {
        if (text == _text && Mesh != null)
        {
            return;
        }
        _text = text;
        int lineCount = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                lineCount++;
            }
        }
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        float lineH = SmallFont.Cell * _pixelSize;
        float y = lineCount * lineH * 0.5f; // top edge (centred vertically)
        float x = LineStartX(text, 0);
        int lineStart = 0;
        int glyphs = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n')
            {
                y -= lineH;
                lineStart = i + 1;
                x = LineStartX(text, lineStart);
                continue;
            }
            if (ch == '\r')
            {
                continue;
            }
            int b = ch < 256 ? ch : '?';
            int w = _font.Widths[b];
            if (w <= 0)
            {
                continue;
            }
            float gw = w * _pixelSize;
            int col = b % SmallFont.Grid;
            int row = b / SmallFont.Grid;
            float texW = _font.Texture.GetWidth();
            float texH = _font.Texture.GetHeight();
            float u0 = col * SmallFont.Cell / texW;
            float v0 = row * SmallFont.Cell / texH;
            float u1 = (col * SmallFont.Cell + w) / texW;
            float v1 = (row + 1) * SmallFont.Cell / texH;

            // Two triangles, CCW facing +Z (top of glyph at current y).
            st.SetUV(new Vector2(u0, v0)); st.AddVertex(new Vector3(x, y, 0.0f));
            st.SetUV(new Vector2(u0, v1)); st.AddVertex(new Vector3(x, y - lineH, 0.0f));
            st.SetUV(new Vector2(u1, v1)); st.AddVertex(new Vector3(x + gw, y - lineH, 0.0f));
            st.SetUV(new Vector2(u0, v0)); st.AddVertex(new Vector3(x, y, 0.0f));
            st.SetUV(new Vector2(u1, v1)); st.AddVertex(new Vector3(x + gw, y - lineH, 0.0f));
            st.SetUV(new Vector2(u1, v0)); st.AddVertex(new Vector3(x + gw, y, 0.0f));
            x += gw;
            glyphs++;
        }
        Mesh = glyphs > 0 ? st.Commit() : null;
    }

    private float LineStartX(string text, int lineStart)
    {
        if (!_center)
        {
            return 0.0f;
        }
        int end = text.IndexOf('\n', lineStart);
        string line = end < 0 ? text[lineStart..] : text[lineStart..end];
        return -_font.MeasureWidth(line) * _pixelSize * 0.5f;
    }
}

/// <summary>Title art helpers: the ui_itemTexts label rows (128x32 each, rows
/// 0..7: BUY NOW, PLAY GAME, OPTIONS, STATISTICS, MODS, OTHER GAMES, QUIT,
/// BACK) and full banner textures (ui_textQuest etc.), as unshaded quads.</summary>
public static class ClassicTitle
{
    public const int RowPlayGame = 1;
    public const int RowOptions = 2;
    public const int RowStatistics = 3;

    /// <summary>Add an ui_itemTexts row quad centred at (0, y, z).</summary>
    public static void BuildRow(Node3D parent, float widthMeters, int row, float y, float z = 0.0f)
    {
        string path = AssetStore.SpritePath("ui_itemTexts.png");
        if (AssetStore.LoadTexture(path) is not Texture2D tex)
        {
            return;
        }
        float rows = tex.GetHeight() / 32.0f;
        parent.AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(widthMeters, widthMeters * (32.0f / 128.0f)) },
            Position = new Vector3(0.0f, y, z),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = tex,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                Uv1Scale = new Vector3(1.0f, 1.0f / rows, 1.0f),
                Uv1Offset = new Vector3(0.0f, row / rows, 0.0f),
                RenderPriority = 59, // over the panel backdrop (58), under text
            },
        });
    }

    /// <summary>Add a full banner texture (e.g. ui_textQuest / ui_textWellDone /
    /// ui_textReaper) centred at (0, y, z), sized by width with the art's aspect.</summary>
    public static MeshInstance3D? BuildBanner(Node3D parent, string file, float widthMeters, float y, float z = 0.0f)
    {
        string path = AssetStore.SpritePath(file);
        if (AssetStore.LoadTexture(path) is not Texture2D tex)
        {
            return null;
        }
        var quad = new MeshInstance3D
        {
            Mesh = new QuadMesh
            {
                Size = new Vector2(widthMeters, widthMeters * tex.GetHeight() / tex.GetWidth()),
            },
            Position = new Vector3(0.0f, y, z),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = tex,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                RenderPriority = 59, // over the panel backdrop (58), under text
            },
        };
        parent.AddChild(quad);
        return quad;
    }
}

/// <summary>
/// The classic ui_menuPanel backdrop as a 3-slice (ui/menu_panel.py): source
/// slices at y=130/150 with a 1px border inset, destination top/bottom slice
/// heights 138/116 scaled by width/510. Built as three quads facing +Z.
/// </summary>
public static class ClassicPanel
{
    // Shared transparent-UI band. World/HUD presentation stays below this;
    // panel art draws first and labels draw last so a transparent panel can
    // never erase its own copy (or let preview sprites punch through it).
    public const int BackdropRenderPriority = 58;
    public const int TextRenderPriority = 68;

    private const float SrcSliceY1 = 130.0f;
    private const float SrcSliceY2 = 150.0f;
    private const float DstTopH = 138.0f;
    private const float DstBottomH = 116.0f;
    private const float Inset = 1.0f;
    private const float InsetWidth = 510.0f;
    private const float InsetHeight = 254.0f;
    // The panel BODY (the dark plate) is off-centre in the art: the left side
    // is the antenna decoration + transparent space. Measured from the alpha
    // channel (columns 183..495, rows 17..242 of the 512x256 texture). The
    // width/height the caller asks for maps to the BODY, centred on the
    // origin, so panel content lines up with the visible plate.
    private const float BodyX0 = 183.0f;
    private const float BodyX1 = 495.0f;
    private const float BodyY0 = 17.0f;
    private const float BodyY1 = 242.0f;

    /// <summary>Add the panel quads to <paramref name="parent"/>: the panel
    /// BODY spans widthMeters x heightMeters centred on the parent origin at
    /// local z (the antenna trim extends beyond to the left/top like the art).</summary>
    public static void Build(Node3D parent, float widthMeters, float heightMeters, float z)
    {
        string path = AssetStore.SpritePath("ui_menuPanel.png");
        if (AssetStore.LoadTexture(path) is not Texture2D tex)
        {
            return;
        }
        float texW = tex.GetWidth();
        float texH = tex.GetHeight();
        // Metres per native texture pixel, so the BODY maps to the asked size.
        float sx = widthMeters / (BodyX1 - BodyX0);
        float sy = heightMeters / (BodyY1 - BodyY0);
        float fullW = InsetWidth * sx;
        float fullH = InsetHeight * sy;
        // Shift so the body centre (not the texture centre) sits at x=0/y=0.
        float xOff = ((Inset + texW - Inset) * 0.5f - (BodyX0 + BodyX1) * 0.5f) * sx;
        float yOff = ((Inset + texH - Inset) * 0.5f - (BodyY0 + BodyY1) * 0.5f) * sy;

        float topH = DstTopH * (fullW / InsetWidth);
        float bottomH = DstBottomH * (fullW / InsetWidth);
        float midH = fullH - topH - bottomH;

        void Quad(float y, float h, float srcY, float srcH)
        {
            if (h <= 0.0f)
            {
                return;
            }
            var mesh = new PlaneMesh
            {
                Size = new Vector2(fullW, h),
                Orientation = PlaneMesh.OrientationEnum.Z,
            };
            var mat = new StandardMaterial3D
            {
                AlbedoTexture = tex,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                Uv1Scale = new Vector3((texW - Inset * 2.0f) / texW, srcH / texH, 1.0f),
                Uv1Offset = new Vector3(Inset / texW, srcY / texH, 0.0f),
                // Panels must draw OVER the HUD (40-44): all diegetic UI is
                // transparent with depth-write off, so priority is the only
                // layering — the panel stack owns 58..67.
                RenderPriority = BackdropRenderPriority,
            };
            parent.AddChild(new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = mat,
                Position = new Vector3(xOff, y - h * 0.5f, z),
            });
        }

        float top = fullH * 0.5f + yOff;
        if (midH <= 0.0f)
        {
            Quad(top, fullH, Inset, texH - Inset * 2.0f);
            return;
        }
        Quad(top, topH, Inset, SrcSliceY1 - Inset);
        Quad(top - topH, midH, SrcSliceY1, SrcSliceY2 - SrcSliceY1);
        Quad(top - topH - midH, bottomH, SrcSliceY2, texH - Inset - SrcSliceY2);
    }
}
