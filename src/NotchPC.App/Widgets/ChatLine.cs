using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NotchPC.Core.Twitch;

namespace NotchPC.App.Widgets;

/// <summary>One chat line: coloured name, then text with emote images inline.</summary>
internal sealed class ChatLine : TextBlock
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(ChatMessage), typeof(ChatLine), new PropertyMetadata(null, (d, _) => ((ChatLine)d).Build()));

    private static readonly Dictionary<string, BitmapImage> Emotes = new(StringComparer.Ordinal);
    private static readonly Brush DefaultName = Frozen(Color.FromRgb(0xA9, 0x8B, 0xFF));

    public ChatLine()
    {
        TextWrapping = TextWrapping.Wrap;
        LineHeight = 20;
    }

    public ChatMessage? Message
    {
        get => (ChatMessage?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>Twitch name colours are chosen for a white page; very dark ones are lightened for the dark card.</summary>
    private static Brush NameBrush(string? hex)
    {
        if (hex is null || ColorConverter.ConvertFromString(hex) is not Color c)
        {
            return DefaultName;
        }

        var luminance = ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255;
        if (luminance < 0.35)
        {
            var k = 0.45;
            c = Color.FromRgb((byte)(c.R + ((255 - c.R) * k)), (byte)(c.G + ((255 - c.G) * k)), (byte)(c.B + ((255 - c.B) * k)));
        }

        return Frozen(c);
    }

    private static BitmapImage Emote(string url)
    {
        if (!Emotes.TryGetValue(url, out var image))
        {
            image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            Emotes[url] = image;
            if (Emotes.Count > 400)
            {
                Emotes.Remove(Emotes.Keys.First());
            }
        }

        return image;
    }

    private void Build()
    {
        Inlines.Clear();
        if (Message is not { } m)
        {
            return;
        }

        var name = NameBrush(m.Color);
        Inlines.Add(new Run(m.User) { Foreground = name, FontWeight = FontWeights.SemiBold });
        Inlines.Add(new Run(m.IsAction ? " " : ": "));
        foreach (var part in m.Parts)
        {
            if (part.EmoteUrl is { } url)
            {
                var image = new Image { Source = Emote(url), Height = 20, ToolTip = part.Text, Margin = new Thickness(1, 0, 1, -4) };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
            }
            else
            {
                Inlines.Add(new Run(part.Text) { Foreground = m.IsAction ? name : Foreground, FontStyle = m.IsAction ? FontStyles.Italic : FontStyles.Normal });
            }
        }
    }
}
