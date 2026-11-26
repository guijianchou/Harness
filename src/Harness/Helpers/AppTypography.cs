// Copyright (c) Lanstack @openclaw. All rights reserved.

using System.ComponentModel;
using Harness.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Harness.Helpers;

/// <summary>Shared, live text preferences. Icon and explicitly monospaced fonts stay independent.</summary>
public sealed class AppTypography : DependencyObject, INotifyPropertyChanged
{
    public static AppTypography Current { get; } = new();

    public FontFamily Family { get; private set; } = new("XamlAutoFontFamily");

    public double Size { get; private set; } = 14;

    private string _familyName = "System UI";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(AppSettings settings)
    {
        var familyName = settings.AppFontFamily == "Noto Sans SC" ? "Noto Sans SC" : "System UI";
        var size = double.IsFinite(settings.AppFontSize) && settings.AppFontSize is >= 10 and <= 32 &&
            settings.AppFontSize == Math.Truncate(settings.AppFontSize) ? settings.AppFontSize : 14;
        if (_familyName != familyName)
        {
            _familyName = familyName;
            Family = new FontFamily(familyName == "Noto Sans SC"
                ? "ms-appx:///Assets/Fonts/NotoSansSC.ttf#Noto Sans SC"
                : "XamlAutoFontFamily");
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Family)));
        }
        if (Size != size)
        {
            Size = size;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Size)));
        }
    }

    // Using bindings instead of rewriting theme resources also updates existing
    // windows, templates and newly opened flyouts without a theme flash or tree scan.
    public static readonly DependencyProperty BaseSizeProperty = DependencyProperty.RegisterAttached(
        "BaseSize", typeof(double), typeof(AppTypography), new PropertyMetadata(0d, OnBaseSizeChanged));

    public static double GetBaseSize(DependencyObject target) => (double)target.GetValue(BaseSizeProperty);

    public static void SetBaseSize(DependencyObject target, double value) => target.SetValue(BaseSizeProperty, value);

    private static void OnBaseSizeChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var properties = target switch
        {
            TextBlock => (TextBlock.FontFamilyProperty, TextBlock.FontSizeProperty),
            RichTextBlock => (RichTextBlock.FontFamilyProperty, RichTextBlock.FontSizeProperty),
            Control => (Control.FontFamilyProperty, Control.FontSizeProperty),
            _ => (null, null),
        };
        if (properties.Item1 is not { } familyProperty || properties.Item2 is not { } sizeProperty)
        {
            return;
        }

        // Respect local fonts (logs, glyphs) and template bindings to a parent.
        if (target.ReadLocalValue(familyProperty) == DependencyProperty.UnsetValue)
        {
            BindingOperations.SetBinding(target, familyProperty, new Binding
            {
                Source = Current,
                Path = new PropertyPath(nameof(Family)),
                Mode = BindingMode.OneWay,
            });
        }
        if (target.ReadLocalValue(sizeProperty) == DependencyProperty.UnsetValue || (double)args.OldValue > 0)
        {
            BindingOperations.SetBinding(target, sizeProperty, new Binding
            {
                Source = Current,
                Path = new PropertyPath(nameof(Size)),
                Mode = BindingMode.OneWay,
                Converter = new FontScaleConverter(),
                ConverterParameter = (double)args.NewValue,
            });
        }
    }

    private sealed class FontScaleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => (double)value * (double)parameter / 14;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
