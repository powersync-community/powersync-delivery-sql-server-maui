namespace DeliveryApp.Views;

/// <summary>A dismissable banner at the top of a page. Hidden while <see cref="Message"/> is null.</summary>
public class ErrorBanner : ContentView
{
    public static readonly BindableProperty MessageProperty = BindableProperty.Create(
        nameof(Message), typeof(string), typeof(ErrorBanner), null, BindingMode.TwoWay,
        propertyChanged: (bindable, _, newValue) => ((ErrorBanner)bindable).Show((string?)newValue));

    readonly Label _label;

    public ErrorBanner()
    {
        // The label carries the "ErrorBanner" id: it's the element UI tests read the message from.
        _label = new Label { AutomationId = "ErrorBanner", TextColor = Colors.White, VerticalOptions = LayoutOptions.Center, FontSize = 14 };

        var dismiss = new Button
        {
            AutomationId = "ErrorBannerDismiss",
            Text = "✕",
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            Padding = new Thickness(8, 0),
            VerticalOptions = LayoutOptions.Center,
        };
        SemanticProperties.SetDescription(dismiss, "Dismiss");
        dismiss.Clicked += (_, _) => Message = null;

        var grid = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            Padding = new Thickness(16, 10, 8, 10),
            ColumnSpacing = 8,
        };
        grid.Add(_label, 0);
        grid.Add(dismiss, 1);

        Content = grid;
        SetDynamicResource(BackgroundColorProperty, "ErrorColor");
        IsVisible = false;
    }

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    void Show(string? message)
    {
        _label.Text = message;
        IsVisible = message is not null;
    }
}
