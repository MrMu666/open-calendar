using System.Windows;

namespace CalendarApp.Views;

/// <summary>
/// Attached property giving a WPF Button a CornerRadius. Button itself has no
/// CornerRadius; the glass styles' ControlTemplate binds this to its Border.
/// </summary>
public static class ButtonRoundness
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(ButtonRoundness),
            new FrameworkPropertyMetadata(new CornerRadius(0),
                FrameworkPropertyMetadataOptions.Inherits));

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) =>
        element.SetValue(CornerRadiusProperty, value);

    public static CornerRadius GetCornerRadius(DependencyObject element) =>
        (CornerRadius)element.GetValue(CornerRadiusProperty);
}
