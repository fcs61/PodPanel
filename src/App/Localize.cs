using System;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using PodPanel.Core;

namespace PodPanel;

/// <summary>
/// XAML'de çevrilmiş metin: Text="{local:T settings}". Dil değişince bağlı her metin kendiliğinden yenilenir.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding("[" + Key + "]") { Source = LocSource.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

/// <summary>Loc'un bağlamaya uygun yüzü: dizinleyici anahtarın metnini verir.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    private LocSource()
    {
        Loc.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Loc.T(key);
}
