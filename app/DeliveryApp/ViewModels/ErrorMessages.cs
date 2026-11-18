using DeliveryApp.Services;

namespace DeliveryApp.ViewModels;

public static class ErrorMessages
{
    public const string Connection = "Couldn't reach the server. Check your connection and try again.";

    /// <summary>The banner text for a failed request. Otherwise the server's own error text (e.g. for a 409).</summary>
    public static string For(ApiException ex) => ex.IsConnectionProblem ? Connection : ex.Message;
}
