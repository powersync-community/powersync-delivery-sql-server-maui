namespace DeliveryApp.Views;

/// <summary>
/// A small pill showing a stop's status: assigned is neutral, delivered green, failed amber, canceled gray.
/// </summary>
public class StatusBadge : ContentView
{
    public static readonly BindableProperty StatusProperty = BindableProperty.Create(
        nameof(Status), typeof(string), typeof(StatusBadge), null,
        propertyChanged: (bindable, _, newValue) => ((StatusBadge)bindable).Show((string?)newValue));

    readonly Label _label;
    readonly Border _border;

    public StatusBadge()
    {
        _label = new Label { TextColor = Colors.White, FontSize = 12, FontAttributes = FontAttributes.Bold };
        _border = new Border
        {
            Content = _label,
            Padding = new Thickness(8, 2),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
        };
        Content = _border;
        HorizontalOptions = LayoutOptions.Start;
        VerticalOptions = LayoutOptions.Center;
    }

    public string? Status
    {
        get => (string?)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    void Show(string? status)
    {
        _label.Text = status;
        _border.SetDynamicResource(BackgroundColorProperty, status switch
        {
            "delivered" => "StatusDelivered",
            "failed" => "StatusFailed",
            "canceled" => "StatusCanceled",
            _ => "StatusAssigned",
        });
        IsVisible = status is not null;
    }
}
