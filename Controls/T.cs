using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using PowerHub.Services;
using Binding = System.Windows.Data.Binding;

namespace PowerHub.Controls;

[MarkupExtensionReturnType(typeof(string))]
public sealed class T(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding(nameof(L.Language))
    {
        Source = L.Current, Mode = BindingMode.OneWay, Converter = Translator.Instance, ConverterParameter = key
    }.ProvideValue(serviceProvider);

    private sealed class Translator : IValueConverter
    {
        internal static readonly Translator Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => L.T((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
