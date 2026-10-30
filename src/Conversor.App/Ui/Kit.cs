using Android.Content;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Util;
using Android.Views;
using Color = Android.Graphics.Color;
using ColorStateList = Android.Content.Res.ColorStateList;
using Typeface = Android.Graphics.Typeface;
using TypefaceStyle = Android.Graphics.TypefaceStyle;

namespace Conversor.App.Ui;

/// <summary>As cores da tela, no tema claro ou escuro do celular.</summary>
internal sealed record Palette(
    Color Background, Color Surface, Color Text, Color Muted, Color Border,
    Color Accent, Color AccentSoft, Color OnAccent, Color Header, Color Success, Color Error)
{
    public static Palette Light { get; } = new(
        Color.ParseColor("#F6F5FB"), Color.ParseColor("#FFFFFF"), Color.ParseColor("#1B1A22"),
        Color.ParseColor("#6B6879"), Color.ParseColor("#E4E2EE"), Color.ParseColor("#5B4FD6"),
        Color.ParseColor("#ECEAFD"), Color.ParseColor("#FFFFFF"), Color.ParseColor("#5B4FD6"),
        Color.ParseColor("#1D9E75"), Color.ParseColor("#C93C3C"));

    public static Palette Dark { get; } = new(
        Color.ParseColor("#121117"), Color.ParseColor("#1D1C24"), Color.ParseColor("#EDEBF5"),
        Color.ParseColor("#A19EB2"), Color.ParseColor("#2E2C38"), Color.ParseColor("#8E85F0"),
        Color.ParseColor("#2A2740"), Color.ParseColor("#121117"), Color.ParseColor("#3A3291"),
        Color.ParseColor("#4CC59B"), Color.ParseColor("#F07070"));

    public static Palette For(Context context)
    {
        var mode = context.Resources?.Configuration?.UiMode ?? Android.Content.Res.UiMode.NightNo;
        return (mode & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes ? Dark : Light;
    }
}

/// <summary>Atalhos para montar a tela em código (sem arquivos de layout).</summary>
internal sealed class Kit(Context context, Palette palette)
{
    public Palette Colors => palette;

    public int Dp(float value) => (int)Math.Round(value * (context.Resources?.DisplayMetrics?.Density ?? 1f));

    public TextView Text(string text, float sizeSp, Color color, bool bold = false)
    {
        var view = new TextView(context) { Text = text };
        view.SetTextSize(ComplexUnitType.Sp, sizeSp);
        view.SetTextColor(color);
        if (bold)
            view.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal), TypefaceStyle.Normal);
        view.SetLineSpacing(0f, 1.15f);
        return view;
    }

    /// <summary>Texto de uma linha que corta no meio quando é longo ("relatório_fin…_2026.pdf").</summary>
    public TextView SingleLine(string text, float sizeSp, Color color, bool bold = false)
    {
        var view = Text(text, sizeSp, color, bold);
        view.SetSingleLine();
        view.Ellipsize = TextUtils.TruncateAt.Middle;
        return view;
    }

    public TextView SectionTitle(string text)
    {
        var view = Text(text.ToUpperInvariant(), 12, palette.Muted, bold: true);
        view.LetterSpacing = 0.08f;
        return view;
    }

    public LinearLayout Column(int paddingDp = 0)
    {
        var layout = new LinearLayout(context) { Orientation = Android.Widget.Orientation.Vertical };
        layout.SetPadding(Dp(paddingDp), Dp(paddingDp), Dp(paddingDp), Dp(paddingDp));
        return layout;
    }

    public LinearLayout Row()
    {
        var layout = new LinearLayout(context) { Orientation = Android.Widget.Orientation.Horizontal };
        layout.SetGravity(GravityFlags.CenterVertical);
        return layout;
    }

    public GradientDrawable Shape(Color fill, float radiusDp, Color? stroke = null, float strokeDp = 1, bool dashed = false)
    {
        var shape = new GradientDrawable();
        shape.SetCornerRadius(Dp(radiusDp));
        shape.SetColor(ColorStateList.ValueOf(fill));
        if (stroke is { } line)
        {
            if (dashed)
                shape.SetStroke(Dp(strokeDp), ColorStateList.ValueOf(line), Dp(6), Dp(4));
            else
                shape.SetStroke(Dp(strokeDp), ColorStateList.ValueOf(line));
        }
        return shape;
    }

    /// <summary>Fundo arredondado que mostra o "toque" (ripple) ao ser pressionado.</summary>
    public Drawable Pressable(Color fill, float radiusDp, Color? stroke = null, float strokeDp = 1, bool dashed = false) =>
        new RippleDrawable(ColorStateList.ValueOf(new Color(palette.Accent.R, palette.Accent.G, palette.Accent.B, (byte)48)),
            Shape(fill, radiusDp, stroke, strokeDp, dashed), Shape(Color.White, radiusDp));

    /// <summary>Um cartão: fundo claro, borda fina, cantos arredondados.</summary>
    public LinearLayout Card()
    {
        var card = Column(16);
        card.Background = Shape(palette.Surface, 20, palette.Border);
        return card;
    }

    /// <summary>Botão principal (cheio, na cor de destaque).</summary>
    public TextView PrimaryButton(string text)
    {
        var button = Text(text, 16, palette.OnAccent, bold: true);
        button.Gravity = GravityFlags.Center;
        button.SetPadding(Dp(16), Dp(15), Dp(16), Dp(15));
        button.Background = Pressable(palette.Accent, 16);
        button.Clickable = true;
        button.Focusable = true;
        return button;
    }

    /// <summary>Botão secundário (só contorno).</summary>
    public TextView SecondaryButton(string text)
    {
        var button = Text(text, 15, palette.Accent, bold: true);
        button.Gravity = GravityFlags.Center;
        button.SetPadding(Dp(14), Dp(11), Dp(14), Dp(11));
        button.Background = Pressable(palette.Surface, 14, palette.Accent);
        button.Clickable = true;
        button.Focusable = true;
        return button;
    }

    public static LinearLayout.LayoutParams Fill(int topMarginPx = 0)
    {
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        parameters.TopMargin = topMarginPx;
        return parameters;
    }

    public static LinearLayout.LayoutParams Weighted(float weight, int leftMarginPx = 0)
    {
        var parameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, weight);
        parameters.LeftMargin = leftMarginPx;
        return parameters;
    }

    public static LinearLayout.LayoutParams Wrap(int leftMarginPx = 0, int topMarginPx = 0)
    {
        var parameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        parameters.LeftMargin = leftMarginPx;
        parameters.TopMargin = topMarginPx;
        return parameters;
    }
}
