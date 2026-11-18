using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using NotchPC.Core.Localization;

namespace NotchPC.App.Localization;

/// <summary>Bindable view of <see cref="Loc"/>: every "[key]" binding refreshes when the language changes.</summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private LocalizationSource()
    {
        Loc.CultureChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static LocalizationSource Instance { get; } = new();

    public string this[string key] => Loc.Get(key);
}

/// <summary>XAML: Text="{loc:Tr Settings.Title}".</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
